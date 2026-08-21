using System.Collections.ObjectModel;
using System.Windows.Media;
using TicketManager.Models;

namespace TicketManager.ViewModels;

/// <summary>一个工单线索节点（包含其折叠后的邮件树）。</summary>
public class ThreadViewModel
{
    public TicketThread Thread { get; }
    public ObservableCollection<EmailNodeViewModel> Children { get; } = new();

    public ThreadViewModel(TicketThread thread, IReadOnlyList<string> supportAddresses, string selfAddress)
    {
        Thread = thread;
        foreach (var r in thread.DisplayRoots)
            Children.Add(new EmailNodeViewModel(r, this, supportAddresses, selfAddress));
    }

    public string TicketNumber => Thread.TicketNumber;
    public string Status => string.IsNullOrEmpty(Thread.Status) ? "未总结" : Thread.Status;
    public string Header => string.IsNullOrEmpty(Thread.TicketNumber)
        ? $"{Thread.Product} · {Thread.LastActivity.LocalDateTime:MM-dd HH:mm}"
        : $"[{Thread.TicketNumber}] · {Thread.LastActivity.LocalDateTime:MM-dd HH:mm}";
    public string Summary => Thread.StatusSummary;
    public string CountText => $"{Thread.EmailCount} 封";

    /// <summary>线索内是否存在新同步的邮件（根线索高亮用）。</summary>
    public bool HasNewMail => Thread.Emails.Any(e => e.IsNew);

    /// <summary>手工设置状态时填写的理由（AI 状态为空串）。</summary>
    public string StatusReason => Thread.StatusReason;

    public Brush StatusBrush => Status switch
    {
        "已解决" => Brushes.ForestGreen,
        "等待客户回复" => Brushes.OrangeRed,
        "等待客服回复" => Brushes.DarkOrange,
        "等待研发回复" => Brushes.DodgerBlue,
        "纳入开发计划" => Brushes.MediumPurple,
        "合并或拆分为其他工单" => Brushes.Teal,
        "处理中" => Brushes.SteelBlue,
        "需升级" => Brushes.Red,
        "新建" => Brushes.Gray,
        _ => Brushes.Gray
    };

    /// <summary>AI 总结框配色：边线用状态主色，背景用对应淡色，与左侧邮件树的状态颜色对齐。</summary>
    public Brush SummaryBorder => SummaryColors.Border;
    public Brush SummaryBackground => SummaryColors.Background;

    private (Brush Border, Brush Background) SummaryColors => Status switch
    {
        "已解决" => (FromHex("#2E7D32"), FromHex("#E8F5E9")),
        "等待客户回复" => (FromHex("#D32F2F"), FromHex("#FFEBEE")),
        "等待客服回复" => (FromHex("#EF6C00"), FromHex("#FFF3E0")),
        "等待研发回复" => (FromHex("#1976D2"), FromHex("#E3F2FD")),
        "纳入开发计划" => (FromHex("#8E24AA"), FromHex("#F3E5F5")),
        "合并或拆分为其他工单" => (FromHex("#0F766E"), FromHex("#F0FDFA")),
        "处理中" => (FromHex("#607D8B"), FromHex("#ECEFF1")),
        "需升级" => (FromHex("#C62828"), FromHex("#FFEBEE")),
        "新建" => (FromHex("#9E9E9E"), FromHex("#F5F5F5")),
        _ => (FromHex("#9E9E9E"), FromHex("#F0F0F0"))
    };

    private static SolidColorBrush FromHex(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
