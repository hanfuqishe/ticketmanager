namespace TicketManager.Models;

/// <summary>一个工单线索 = 一个具体问题的全部邮件往来。</summary>
public class TicketThread
{
    public long Id { get; set; }
    public string TicketNumber { get; set; } = "";
    public string Product { get; set; } = "";
    public string Enterprise { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusSummary { get; set; } = "";

    public DateTimeOffset FirstActivity { get; set; }
    public DateTimeOffset LastActivity { get; set; }

    public List<EmailMessage> Emails { get; set; } = new();

    /// <summary>展示树的根节点列表（按折叠规则构造）。</summary>
    public List<ThreadNode> DisplayRoots { get; set; } = new();

    public int EmailCount => Emails.Count;
}
