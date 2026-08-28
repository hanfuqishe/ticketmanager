using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.ViewModels;

/// <summary>提新工单窗口：选择 客服收件人/产品/客户/接口人，填写故障现象与详情，通过 Zoho 发送邮件。
/// 邮件标题：[产品][客户]故障现象；收件人=客服邮箱；发件人=当前账号；抄送=客户接口人。</summary>
public class SubmitTicketViewModel : ViewModelBase
{
    private readonly WorkflowService _workflow;
    private readonly List<EmailSignature> _signatures;

    public List<string> SupportRecipients { get; }
    public List<string> Products { get; }
    public List<string> Customers { get; }
    public ObservableCollection<string> Contacts { get; } = new();

    /// <summary>抄送候选：我方同事（设置→我方域名），选中后自动追加到抄送栏。</summary>
    public List<string> ColleagueCcOptions { get; }

    /// <summary>可选签名名（在“设置→签名”中维护）。</summary>
    public List<string> SignatureNames { get; }

    private string _selectedSignature = "";
    public string SelectedSignature
    {
        get => _selectedSignature;
        set
        {
            if (!Set(ref _selectedSignature, value)) return;
            SelectedSignatureObject = _signatures.FirstOrDefault(s => s.Name == value);
            OnPropertyChanged(nameof(SelectedSignatureObject)); // 通知预览重新绑定签名内容
        }
    }

    /// <summary>当前选中的签名对象（供只读预览）。</summary>
    public EmailSignature? SelectedSignatureObject { get; private set; }

    /// <summary>全局邮件字体（统一应用于 邮件正文 与 签名，在“设置→字体…”中维护）。</summary>
    public string EmailFontFamily => _workflow.Config.EmailFontFamily;
    public double EmailFontSize => _workflow.Config.EmailFontSize;

    /// <summary>全局邮件字体颜色（hex 转 Brush，供预览）。</summary>
    public Brush EmailFontBrush
    {
        get
        {
            var hex = _workflow.Config.EmailFontColor;
            if (string.IsNullOrWhiteSpace(hex)) hex = "#333333";
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return new SolidColorBrush(Colors.DimGray); }
        }
    }

    private string _selectedRecipient = "";
    public string SelectedRecipient
    {
        get => _selectedRecipient;
        set { if (Set(ref _selectedRecipient, value)) OnPropertyChanged(nameof(SubjectPreview)); }
    }

    private string _selectedProduct = "";
    public string SelectedProduct
    {
        get => _selectedProduct;
        set { if (Set(ref _selectedProduct, value)) OnPropertyChanged(nameof(SubjectPreview)); }
    }

    private string _customer = "";
    public string Customer
    {
        get => _customer;
        set { if (Set(ref _customer, value)) OnPropertyChanged(nameof(SubjectPreview)); }
    }

    private string _selectedContact = "";
    public string SelectedContact { get => _selectedContact; set => Set(ref _selectedContact, value); }

    private string _ccEmails = "";
    public string CcEmails { get => _ccEmails; set => Set(ref _ccEmails, value); }

    private string _fault = "";
    public string Fault
    {
        get => _fault;
        set { if (Set(ref _fault, value)) OnPropertyChanged(nameof(SubjectPreview)); }
    }

    private string _body = "";
    public string Body { get => _body; set => Set(ref _body, value); }

    /// <summary>待发送的附件（完整文件路径）。</summary>
    public ObservableCollection<string> Attachments { get; } = new();

    /// <summary>附件数量摘要（用于状态栏提示）。</summary>
    public string AttachmentSummary => Attachments.Count == 0 ? "未添加附件" : $"已添加 {Attachments.Count} 个附件";

    /// <summary>选择并添加附件（可多选）。</summary>
    public void AddAttachment()
    {
        var dlg = new OpenFileDialog { Title = "选择要附加的文件", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            if (!Attachments.Contains(f)) Attachments.Add(f);
        OnPropertyChanged(nameof(AttachmentSummary));
    }

    /// <summary>移除指定附件。</summary>
    public void RemoveAttachment(object? item)
    {
        if (item is string f)
        {
            Attachments.Remove(f);
            OnPropertyChanged(nameof(AttachmentSummary));
        }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _isSending;
    public bool IsSending { get => _isSending; set => Set(ref _isSending, value); }

    /// <summary>最终邮件标题预览：[产品][客户]故障现象。</summary>
    public string SubjectPreview =>
        $"[{SelectedProduct?.Trim()}][{Customer?.Trim()}]{Fault?.Trim()}".Trim();

    public ICommand SendCommand { get; }

    /// <summary>发送成功时触发（窗口据此关闭）。</summary>
    public event Action? Sended;

    public SubmitTicketViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        SupportRecipients = workflow.FormatRecipients(workflow.GetSupportRecipients());
        Products = workflow.GetKnownProducts();
        Customers = workflow.GetKnownEnterprises();
        SelectedRecipient = SupportRecipients.FirstOrDefault() ?? "";
        SelectedProduct = Products.FirstOrDefault() ?? "";
        Customer = Customers.FirstOrDefault() ?? "";
        ColleagueCcOptions = workflow.FormatRecipients(workflow.GetColleagueContacts());
        LoadContacts();
        _signatures = workflow.LoadSignatures();
        SignatureNames = _signatures.Select(s => s.Name).ToList();
        SelectedSignature = SignatureNames.FirstOrDefault() ?? "";
        SendCommand = new RelayCommand(async _ => await SendAsync(), _ => CanSend);
    }

    private bool CanSend =>
        !IsSending && !string.IsNullOrWhiteSpace(SelectedRecipient) &&
        !string.IsNullOrWhiteSpace(Customer) && !string.IsNullOrWhiteSpace(Fault);

    /// <summary>按当前客户刷新接口人下拉（客户下拉失焦/切换时调用）。</summary>
    public void LoadContacts()
    {
        var cust = Customer?.Trim() ?? "";
        var list = _workflow.FormatRecipients(_workflow.GetCustomerContacts(cust));
        var prev = SelectedContact;
        Contacts.Clear();
        foreach (var c in list) Contacts.Add(c);
        SelectedContact = Contacts.Contains(prev) ? prev : (Contacts.FirstOrDefault() ?? "");
    }

    private async Task SendAsync()
    {
        // 下拉项可能是 “姓名 <邮箱>”，发送前解析出纯邮箱
        var recipient = WorkflowService.ExtractEmail(SelectedRecipient);
        var customer = Customer?.Trim() ?? "";
        var fault = Fault?.Trim() ?? "";
        if (string.IsNullOrEmpty(recipient) || string.IsNullOrEmpty(customer) || string.IsNullOrEmpty(fault))
        {
            StatusText = "请填写 收信人、客户 和 故障现象";
            return;
        }
        var subject = $"[{SelectedProduct?.Trim()}][{customer}]{fault}";
        IsSending = true;
        StatusText = "正在发送…";
        try
        {
            // 抄送 = 客户接口人 + 手动抄送栏（多个邮箱，逗号/分号分隔，支持 姓名<邮箱> 格式）
            var ccList = new List<string>();
            var contact = WorkflowService.ExtractEmail(SelectedContact);
            if (!string.IsNullOrWhiteSpace(contact)) ccList.Add(contact);
            foreach (var a in (CcEmails ?? "").Split(';', ','))
            {
                var addr = WorkflowService.ExtractEmail(a);
                if (addr.Length > 0) ccList.Add(addr);
            }
            var cc = string.Join(",", ccList.Distinct(StringComparer.OrdinalIgnoreCase)); // Zoho 发信只接受逗号分隔（分号会报“收件人地址中含有特殊字符”）
            var sig = _signatures.FirstOrDefault(s => s.Name == SelectedSignature);
            var content = BuildBodyHtml(Body ?? "", sig);
            var (ok, err) = await _workflow.SendTicketEmailAsync(
                recipient, string.IsNullOrEmpty(cc) ? null : cc, subject, content, Attachments.Count > 0 ? Attachments.ToList() : null, default);
            StatusText = ok ? $"发送成功：{subject}" : (err ?? "发送失败");
            if (ok) Sended?.Invoke();
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>把 纯文本正文 + 签名 组装成 HTML（正文与签名统一用全局邮件字体渲染）。</summary>
    private string BuildBodyHtml(string? body, EmailSignature? sig)
    {
        var cfg = _workflow.Config;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.Append("<div style=\"font-family:'")
              .Append(WebUtility.HtmlEncode(cfg.EmailFontFamily))
              .Append("';font-size:").Append(cfg.EmailFontSize)
              .Append("pt;color:").Append(WebUtility.HtmlEncode(cfg.EmailFontColor)).Append(";\">")
              .Append(WebUtility.HtmlEncode(body)
                .Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>"))
              .Append("</div>");
        }
        if (sig != null && !string.IsNullOrWhiteSpace(sig.Text))
        {
            var sigText = WebUtility.HtmlEncode(sig.Text)
                .Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
            sb.Append("<div style=\"margin-top:14px;font-family:'")
              .Append(WebUtility.HtmlEncode(cfg.EmailFontFamily))
              .Append("';font-size:").Append(cfg.EmailFontSize)
              .Append("pt;color:").Append(WebUtility.HtmlEncode(cfg.EmailFontColor)).Append(";\">")
              .Append(sigText).Append("</div>");
        }
        return sb.ToString();
    }
}
