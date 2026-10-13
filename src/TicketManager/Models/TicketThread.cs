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

    /// <summary>手工设置状态时填写的理由（AI 总结状态时为空串）。</summary>
    public string StatusReason { get; set; } = "";

    public DateTimeOffset FirstActivity { get; set; }
    public DateTimeOffset LastActivity { get; set; }

    public List<EmailMessage> Emails { get; set; } = new();

    /// <summary>展示树的根节点列表（按折叠规则构造）。</summary>
    public List<ThreadNode> DisplayRoots { get; set; } = new();

    public int EmailCount => Emails.Count;

    // ===== 状态三态：打开 / 关闭 / 搁置 =====
    //   打开：还要继续跟进（新建、处理中、等待客户回复、等待客服回复、等待研发回复、需升级…）
    //   关闭：这件事已经了结（已解决、已完成、已关闭、合并或拆分为其他工单）
    //   搁置：事情没完但不用跟（纳入开发计划 = 等产品排期；已搁置 = 长时间没往来，自动置入）
    // “仅打开的工单”过滤与智能折叠都只看 IsOpen：关闭与搁置都算“无需继续关注”。

    /// <summary>“已搁置”状态名：打开态的线索超过 <see cref="StaleAfter"/> 没有新邮件往来时自动置为该状态。</summary>
    public const string Shelved = "已搁置";

    /// <summary>打开态的线索超过这么久没有新邮件往来 → 自动“已搁置”。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>关闭态状态：这件事已经了结。</summary>
    public static readonly IReadOnlySet<string> ClosedStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "已解决", "已完成", "已关闭", "合并或拆分为其他工单"
    };

    /// <summary>搁置态状态：事情没完但不用跟（“纳入开发计划”= 等排期；<see cref="Shelved"/> = 长时间没往来自动置入）。</summary>
    public static readonly IReadOnlySet<string> ShelvedStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "纳入开发计划", Shelved
    };

    /// <summary>三态之一。</summary>
    public ThreadState State
        => ClosedStatuses.Contains(Status ?? "") ? ThreadState.Closed
         : ShelvedStatuses.Contains(Status ?? "") ? ThreadState.Shelved
         : ThreadState.Open;   // 空状态、以及将来新增的未知状态都按打开处理（宁可多跟，别漏）

    /// <summary>是否打开态（还要继续跟进）。</summary>
    public bool IsOpen => State == ThreadState.Open;

    /// <summary>是否关闭态（已了结）。</summary>
    public bool IsClosed => State == ThreadState.Closed;

    /// <summary>是否搁置态（纳入开发计划 / 已搁置）。</summary>
    public bool IsShelved => State == ThreadState.Shelved;

    /// <summary>是否“无需继续关注”（关闭态或搁置态）：“仅打开的工单”过滤与智能折叠据此判断。</summary>
    public bool IsInactive => State != ThreadState.Open;

    /// <summary>按“最近一次往来距今超过 <see cref="StaleAfter"/>”判断是否应自动搁置（只判断，不改状态）。
    /// 只管打开态：关闭态已经了结，搁置态本来就不用跟，都不该被改标签。</summary>
    public bool ShouldShelve(DateTimeOffset now) => IsOpen && LastActivity != default && now - LastActivity > StaleAfter;

    /// <summary>自动搁置：状态置为「已搁置」并写明理由。</summary>
    public void Shelve() => (Status, StatusReason) = (Shelved, "超过 1 个月无新邮件往来，自动搁置");
}

/// <summary>线索状态三态。</summary>
public enum ThreadState
{
    /// <summary>打开：还要继续跟进。</summary>
    Open,
    /// <summary>关闭：已经了结。</summary>
    Closed,
    /// <summary>搁置：事情没完但不用跟（等排期 / 长时间无往来）。</summary>
    Shelved
}
