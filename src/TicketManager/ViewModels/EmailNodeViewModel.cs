using System.Collections.ObjectModel;
using System.Windows.Media;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.ViewModels;

/// <summary>发件人角色，用于在邮件树中配色区分。</summary>
public enum SenderRole { Self, Support, Customer }

/// <summary>展示树中的一封邮件节点（递归）。</summary>
public class EmailNodeViewModel
{
    public EmailMessage Email { get; }
    public ThreadViewModel ThreadOwner { get; }
    public int Depth { get; }
    public ObservableCollection<EmailNodeViewModel> Children { get; } = new();

    public EmailNodeViewModel(ThreadNode node, ThreadViewModel owner,
        IReadOnlyList<string> supportAddresses, string selfAddress)
    {
        Email = node.Email;
        ThreadOwner = owner;
        Depth = node.Depth;
        Role = Classify(Email.FromAddress, supportAddresses, selfAddress);
        foreach (var c in node.Children)
            Children.Add(new EmailNodeViewModel(c, owner, supportAddresses, selfAddress));
    }

    public SenderRole Role { get; }

    public Brush SenderBrush => Role switch
    {
        SenderRole.Self => Brushes.ForestGreen,     // 自己
        SenderRole.Support => Brushes.DodgerBlue,   // 客服/技术支持
        _ => Brushes.DarkOrange                     // 客户
    };

    private static SenderRole Classify(string from, IReadOnlyList<string> support, string self)
    {
        if (!string.IsNullOrEmpty(from) && string.Equals(from, self, StringComparison.OrdinalIgnoreCase))
            return SenderRole.Self;
        if (support.Any(s => string.Equals(from, s, StringComparison.OrdinalIgnoreCase)))
            return SenderRole.Support;
        return SenderRole.Customer;
    }

    /// <summary>展示标题：根邮件显示剥离 工单号/产品/客户 后的纯主题；其余优先 AI 标题，否则原标题。</summary>
    public string Title => Depth == 0
        ? PureSubject
        : (string.IsNullOrEmpty(Email.AiTitle) ? Email.Subject : Email.AiTitle);

    /// <summary>纯主题：去掉 [工单号][产品][客户] 等前缀，只保留故障现象部分。</summary>
    public string PureSubject
    {
        get
        {
            var parsed = SubjectParser.Parse(Email.Subject);
            return parsed != null && !string.IsNullOrWhiteSpace(parsed.Fault)
                ? parsed.Fault
                : Email.Subject.Trim();
        }
    }

    public string Badge => HasAiTitle ? "✨ " : "";
    public bool HasAiTitle => !string.IsNullOrEmpty(Email.AiTitle);
    public string Sender => Email.DisplaySender;
    public string Time => Email.DateSent.LocalDateTime.ToString("MM-dd HH:mm");
    public string SubLine => $"{Sender} · {Time}";
    public string Meta =>
        $"发件人：{Sender}\n" +
        $"收件人：{Email.ToAddresses}\n" +
        $"抄送：{Email.CcAddresses}\n" +
        $"时间：{Email.DateSent:yyyy-MM-dd HH:mm}\n" +
        $"原标题：{Email.Subject}";
    public string Body => Email.BodyText;
}
