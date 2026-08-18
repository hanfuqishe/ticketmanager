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

    public Brush StatusBrush => Status switch
    {
        "已解决" => Brushes.ForestGreen,
        "等待客户回复" => Brushes.OrangeRed,
        "等待客服回复" => Brushes.DarkOrange,
        "等待研发回复" => Brushes.DodgerBlue,
        "处理中" => Brushes.SteelBlue,
        "需升级" => Brushes.Red,
        "新建" => Brushes.Gray,
        _ => Brushes.Gray
    };
}
