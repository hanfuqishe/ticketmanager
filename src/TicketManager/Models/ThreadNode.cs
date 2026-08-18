namespace TicketManager.Models;

/// <summary>线程展示树中的一个节点（已应用折叠规则后的显示层级）。</summary>
public class ThreadNode
{
    public EmailMessage Email { get; }
    public int Depth { get; }
    public List<ThreadNode> Children { get; } = new();

    public ThreadNode(EmailMessage email, int depth)
    {
        Email = email;
        Depth = depth;
    }
}
