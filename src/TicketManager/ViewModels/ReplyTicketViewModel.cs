using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.ViewModels;

/// <summary>回复对象：客服 或 客户接口人。</summary>
public sealed class ReplyRecipient
{
    public string Role { get; init; } = "";
    public string Email { get; init; } = "";
    public string Label => $"{Role} <{Email}>";
}

/// <summary>回复邮件：可指定收信人=客服 或 客户接口人（另一个自动作为抄送）；引用只带头部；可一键翻译为英文。</summary>
public class ReplyTicketViewModel : ViewModelBase
{
    private readonly WorkflowService _workflow;

    public List<ReplyRecipient> Recipients { get; }
    public List<string> SignatureNames { get; }

    /// <summary>回复标题：Re: 原标题（保留工单号，便于回收到同一线索）。</summary>
    public string Subject
    {
        get => _subject;
        set => Set(ref _subject, value);
    }
    private string _subject = "";

    /// <summary>引用头部（只含 发件人/时间/收件人/主题，让收信人知道回复的是哪封）。</summary>
    public string QuoteHeader { get; }

    /// <summary>回复对象（客服/客户接口人），切换时自动更新抄送。</summary>
    public ReplyRecipient? SelectedRecipient
    {
        get => _selectedRecipient;
        set
        {
            if (!Set(ref _selectedRecipient, value)) return;
            OnPropertyChanged(nameof(CcDisplay));
            OnPropertyChanged(nameof(CanSend));
        }
    }
    private ReplyRecipient? _selectedRecipient;

    /// <summary>抄送邮箱（其余所有候选人，用逗号连接——Zoho 发信接口只接受逗号分隔，分号会报“收件人地址中含有特殊字符”）。</summary>
    public string CcEmails => string.Join(",", Recipients
        .Where(r => !ReferenceEquals(r, SelectedRecipient))
        .Select(r => WorkflowService.ExtractEmail(r.Email))
        .Where(e => e.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase));
    public string CcDisplay => string.IsNullOrEmpty(CcEmails) ? "（无）" : CcEmails;

    public string Body
    {
        get => _body;
        set { if (Set(ref _body, value)) OnPropertyChanged(nameof(CanSend)); }
    }
    private string _body = "";

    public string Signature { get => _signature; set => Set(ref _signature, value); }
    private string _signature = "";

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    private string _statusText = "";

    public bool IsSending
    {
        get => _isSending;
        set { if (Set(ref _isSending, value)) OnPropertyChanged(nameof(CanSend)); }
    }
    private bool _isSending;

    public bool IsTranslating
    {
        get => _isTranslating;
        set { if (Set(ref _isTranslating, value)) OnPropertyChanged(nameof(CanSend)); }
    }
    private bool _isTranslating;

    /// <summary>收信人纯邮箱（提纯：万一含“姓名 <邮箱>”也解析出纯邮箱）。</summary>
    public string RecipientEmail => WorkflowService.ExtractEmail(SelectedRecipient?.Email ?? "");

    /// <summary>发送按钮可用条件：收件人有效即可（正文可为空，空正文点发送会提示填写）。</summary>
    public bool CanSend => !IsSending && !IsTranslating && !string.IsNullOrWhiteSpace(RecipientEmail);

    // 全局邮件字体（与提新工单一致）
    public string EmailFontFamily => _workflow.Config.EmailFontFamily;
    public double EmailFontSize => _workflow.Config.EmailFontSize;

    public ICommand SendCommand { get; }
    public ICommand TranslateCommand { get; }

    public event Action? Sended;

    public ReplyTicketViewModel(WorkflowService workflow, EmailMessage email)
    {
        _workflow = workflow;

        var orig = email.Subject ?? "";
        _subject = Regex.IsMatch(orig, @"^\s*(re|回复|答复)\s*[:：]", RegexOptions.IgnoreCase) ? orig : "Re: " + orig;
        QuoteHeader = BuildQuoteHeader(email);

        // 收信人候选：客服 与 客户接口人（可多个，每个为一选项）；默认回复客服；
        // 若被回复邮件本身来自某客服邮箱则优先回它；抄送=其余所有候选人（支持多抄送）
        var supports = workflow.GetSupportRecipients();
        var support = supports.FirstOrDefault(s => string.Equals(s, email.FromAddress, StringComparison.OrdinalIgnoreCase))
                      ?? supports.FirstOrDefault() ?? "";
        Recipients = new List<ReplyRecipient>();
        if (!string.IsNullOrEmpty(support))
            Recipients.Add(new ReplyRecipient { Role = "客服", Email = support });
        foreach (var c in workflow.GetCustomerContacts(email.Enterprise))
            Recipients.Add(new ReplyRecipient { Role = "客户接口人", Email = c });
        if (Recipients.Count == 0)
            Recipients.Add(new ReplyRecipient { Role = "客服", Email = "" });
        _selectedRecipient = Recipients.FirstOrDefault(r => r.Role == "客服") ?? Recipients[0];

        var sigs = workflow.LoadSignatures();
        SignatureNames = sigs.Select(s => s.Name).ToList();
        _signature = SignatureNames.FirstOrDefault() ?? "";

        SendCommand = new RelayCommand(async _ => await SendAsync(), _ => CanSend);
        TranslateCommand = new RelayCommand(async _ => await TranslateAsync(), _ => !IsTranslating && !IsSending);
    }

    private static string BuildQuoteHeader(EmailMessage e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---------- 原始邮件 ----------");
        if (!string.IsNullOrWhiteSpace(e.DisplaySender)) sb.AppendLine($"发件人：{e.DisplaySender}");
        sb.AppendLine($"发送时间：{e.DateSent:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(e.ToAddresses)) sb.AppendLine($"收件人：{e.ToAddresses}");
        sb.AppendLine($"主题：{e.Subject}");
        return sb.ToString().TrimEnd();
    }

    private async Task SendAsync()
    {
        var to = RecipientEmail;
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(Body))
        {
            StatusText = "请填写 回复内容 与 收信人";
            return;
        }
        IsSending = true;
        try
        {
            var cc = string.IsNullOrEmpty(CcEmails) ? null : CcEmails;
            var content = BuildBodyHtml();
            var (ok, err) = await _workflow.SendTicketEmailAsync(to, cc, Subject, content, null, default);
            StatusText = ok ? $"已发送：{Subject}" : (err ?? "发送失败");
            if (ok) Sended?.Invoke();
        }
        finally { IsSending = false; }
    }

    /// <summary>把正文（+含中文的标题）翻译成英文。</summary>
    private async Task TranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(Body) && !SubjectParser.ContainsCjk(Subject))
        {
            StatusText = "没有可翻译的内容";
            return;
        }
        if (string.IsNullOrEmpty(_workflow.Config.DeepSeekApiKey))
        {
            StatusText = "未配置 DeepSeek API Key，无法翻译";
            return;
        }
        IsTranslating = true;
        StatusText = "正在翻译为英文…";
        try
        {
            using var ai = new DeepSeekService(_workflow.Config);
            if (!string.IsNullOrWhiteSpace(Body))
            {
                var tb = await ai.TranslateToEnglishAsync(Body);
                if (!string.IsNullOrWhiteSpace(tb)) Body = tb;
            }
            if (SubjectParser.ContainsCjk(Subject))
            {
                var ns = await TranslateSubjectAsync(Subject);
                if (!string.IsNullOrWhiteSpace(ns)) Subject = ns;
            }
            StatusText = "已翻译为英文（可再手动修改）";
        }
        finally { IsTranslating = false; }
    }

    /// <summary>翻译标题：保留 Re: 前缀与开头的工单号标记（保证回信仍归回线索），只翻译描述部分。</summary>
    private async Task<string> TranslateSubjectAsync(string subject)
    {
        var mRe = Regex.Match(subject, @"^\s*(re|回复|答复)\s*[:：]\s*", RegexOptions.IgnoreCase);
        var head = mRe.Success ? mRe.Value : "";
        var rest = mRe.Success ? subject[mRe.Length..] : subject;
        var ticketTokens = new List<string>();
        rest = Regex.Replace(rest, @"^\[#{1,3}\s*[A-Za-z0-9\-]+\s*#{1,3}\]", m => { ticketTokens.Add(m.Value); return ""; });
        rest = rest.Trim();
        if (string.IsNullOrEmpty(rest)) return subject;
        using var ai = new DeepSeekService(_workflow.Config);
        var translated = await ai.TranslateToEnglishAsync(rest);
        if (string.IsNullOrWhiteSpace(translated)) return subject;
        return head + string.Concat(ticketTokens) + " " + translated.Trim();
    }

    /// <summary>正文 + 签名 + 引用头部 组装 HTML（正文/签名用全局字体，引用为灰色小字放最后）。</summary>
    private string BuildBodyHtml()
    {
        var cfg = _workflow.Config;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Body))
        {
            sb.Append("<div style=\"font-family:'").Append(WebUtility.HtmlEncode(cfg.EmailFontFamily))
              .Append("';font-size:").Append(cfg.EmailFontSize)
              .Append("pt;color:").Append(WebUtility.HtmlEncode(cfg.EmailFontColor)).Append(";\">")
              .Append(WebUtility.HtmlEncode(Body).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>"))
              .Append("</div>");
        }
        var sig = _workflow.LoadSignatures().FirstOrDefault(s => s.Name == Signature);
        if (sig != null && !string.IsNullOrWhiteSpace(sig.Text))
        {
            var sigText = WebUtility.HtmlEncode(sig.Text).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
            sb.Append("<div style=\"margin-top:14px;font-family:'").Append(WebUtility.HtmlEncode(cfg.EmailFontFamily))
              .Append("';font-size:").Append(cfg.EmailFontSize)
              .Append("pt;color:").Append(WebUtility.HtmlEncode(cfg.EmailFontColor)).Append(";\">")
              .Append(sigText).Append("</div>");
        }
        sb.Append("<div style=\"margin-top:16px;padding-top:8px;border-top:1px solid #ccc;color:#888888;font-size:10pt;font-family:'")
          .Append(WebUtility.HtmlEncode(cfg.EmailFontFamily)).Append("';\">")
          .Append(WebUtility.HtmlEncode(QuoteHeader).Replace("\r\n", "<br>").Replace("\n", "<br>"))
          .Append("</div>");
        return sb.ToString();
    }
}
