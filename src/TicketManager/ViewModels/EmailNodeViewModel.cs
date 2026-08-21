using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.ViewModels;

/// <summary>发件人角色，用于在邮件树中配色区分。</summary>
public enum SenderRole { Self, Support, Customer }

/// <summary>展示树中的一封邮件节点（递归）。</summary>
public class EmailNodeViewModel : ViewModelBase
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

    /// <summary>展示标题：根邮件优先显示 AI 标题（英文主题翻译成中文），否则显示剥离标签后的纯主题；其余优先 AI 标题，否则原标题。</summary>
    public string Title => IsRoot
        ? (string.IsNullOrEmpty(Email.AiTitle) ? PureSubject : Email.AiTitle)
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
    /// <summary>行上显示的时间：根邮件（工单树）同时显示 首封时间 与 最后更新时间；其余显示本邮件时间。</summary>
    public string Time => IsRoot ? RootTime : Email.DateSent.LocalDateTime.ToString("MM-dd HH:mm");
    public string SubLine => $"{Sender} · {Time}";

    /// <summary>工单树根行的时间：创建（首封）时间 + 最后更新时间；两者相同则只显示一个。</summary>
    private string RootTime
    {
        get
        {
            var first = Email.DateSent.LocalDateTime;
            var last = ThreadOwner.Thread.LastActivity.LocalDateTime;
            return first == last
                ? $"{first:MM-dd HH:mm}"
                : $"创建 {first:MM-dd HH:mm} · 更新 {last:MM-dd HH:mm}";
        }
    }

    /// <summary>是否为线程根邮件（第 0 层）。</summary>
    public bool IsRoot => Depth == 0;

    /// <summary>本邮件是否为最近同步新增（用于高亮与跳转）。</summary>
    public bool IsNew => Email.IsNew;

    /// <summary>根邮件且线索内含新同步邮件（用于根线索高亮）。</summary>
    public bool IsNewRoot => IsRoot && ThreadOwner.HasNewMail;

    /// <summary>新邮件徽章可见性（非根的新邮件在行首显示 NEW）。</summary>
    public Visibility NewBadgeVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>新邮件用加粗字体标记：本邮件是新邮件，或其根线索含新邮件时标题加粗。</summary>
    public FontWeight TitleWeight => (IsNew || IsNewRoot) ? FontWeights.Bold : FontWeights.Normal;

    /// <summary>“新邮件/有新增”标记变化后刷新本节点与子节点（不清除时逐个刷新加粗）。</summary>
    public void RefreshNewState()
    {
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsNewRoot));
        OnPropertyChanged(nameof(NewBadgeVisibility));
        OnPropertyChanged(nameof(TitleWeight));
        foreach (var c in Children) c.RefreshNewState();
    }

    private bool _isMultiSelected;
    /// <summary>是否被 Ctrl/Shift 多选（仅根线索高亮）。</summary>
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => Set(ref _isMultiSelected, value);
    }

    /// <summary>邮件节点默认展开（按“展开层次”设置；通常收起）。</summary>
    public bool ExpandedByDefault { get; set; } = false;

    /// <summary>工单状态（仅根邮件显示），非根返回空。</summary>
    public string Status => IsRoot ? ThreadOwner.Status : "";

    /// <summary>状态徽章颜色（仅根邮件）。</summary>
    public Brush StatusBrush => IsRoot ? ThreadOwner.StatusBrush : Brushes.Transparent;

    /// <summary>AI 总结文字颜色：与线索状态色对齐（仅根邮件）。</summary>
    public Brush SummaryBrush => IsRoot ? ThreadOwner.StatusBrush : Brushes.Transparent;

    /// <summary>工单号前缀（仅根邮件）：如 [308843]。</summary>
    public string TicketPrefix =>
        IsRoot && !string.IsNullOrEmpty(ThreadOwner.TicketNumber) ? $"[{ThreadOwner.TicketNumber}]  " : "";

    /// <summary>工单总结（仅根邮件）。</summary>
    public string Summary => IsRoot ? ThreadOwner.Summary : "";

    /// <summary>AI 总结前的分隔符（仅根邮件且有总结时显示）。</summary>
    public string SummaryPrefix => IsRoot && !string.IsNullOrEmpty(ThreadOwner.Summary) ? " · " : "";

    /// <summary>是否有总结需要展示（根邮件且非空）。</summary>
    public bool HasSummary => IsRoot && !string.IsNullOrEmpty(ThreadOwner.Summary);
    public string Meta =>
        $"发件人：{Sender}\n" +
        $"收件人：{Email.ToAddresses}\n" +
        $"抄送：{Email.CcAddresses}\n" +
        $"时间：{Email.DateSent:yyyy-MM-dd HH:mm}\n" +
        $"原标题：{Email.Subject}";
    public string Body => Email.BodyText;

    /// <summary>线程状态等变化后刷新本节点（根邮件）的状态/总结显示，不重建树。</summary>
    public void RefreshThreadInfo()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(SummaryBrush));
        OnPropertyChanged(nameof(TicketPrefix));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(SummaryPrefix));
    }
}
