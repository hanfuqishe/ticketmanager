using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// 线程重建引擎（方案 A）：
/// 1. 按工单号分组（无工单号的通过 References/In-Reply-To 溯源继承归属）；
/// 2. 组内用 In-Reply-To/References 构建真实父子回复树；
/// 3. 应用“折叠规则”生成展示树：
///    - 首封（根）为第 0 层；
///    - 后续邮件一律缩进一次（第 1 层）；
///    - 若某封邮件只有一人回复（单链），不继续缩进（折叠为同级）；
///    - 若 ≥2 人同时回复同一封邮件，则这些分支再缩进一次。
/// </summary>
public class ThreadBuilder
{
    public List<TicketThread> Build(List<EmailMessage> allEmails)
    {
        // MessageId 索引（不区分大小写）
        var index = new Dictionary<string, EmailMessage>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEmails)
            if (!string.IsNullOrEmpty(e.MessageId) && !index.ContainsKey(e.MessageId))
                index[e.MessageId] = e;

        // 为每封邮件确定所属工单
        var groups = new Dictionary<string, List<EmailMessage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEmails)
        {
            var ticket = !string.IsNullOrEmpty(e.TicketNumber)
                ? e.TicketNumber
                : ResolveTicket(e, index) ?? ResolveTicketFromDescendants(e, allEmails, index);
            var key = string.IsNullOrEmpty(ticket)
                ? $"__untitled__|{e.Enterprise}|{e.Product}"
                : ticket;
            if (!groups.TryGetValue(key, out var list)) { list = new List<EmailMessage>(); groups[key] = list; }
            list.Add(e);
        }

        var threads = groups.Select(g => BuildThread(g.Value)).ToList();
        foreach (var t in threads)
        {
            t.FirstActivity = t.Emails.Min(e => e.DateSent);
            t.LastActivity = t.Emails.Max(e => e.DateSent);
        }
        return threads.OrderByDescending(t => t.LastActivity).ToList();
    }

    /// <summary>按已持久化的 ThreadId 分组重建展示树（用于加载时保留状态字段）。</summary>
    public Dictionary<long, List<ThreadNode>> BuildDisplayByThread(List<EmailMessage> allEmails)
    {
        var result = new Dictionary<long, List<ThreadNode>>();
        foreach (var grp in allEmails.Where(e => e.ThreadId > 0).GroupBy(e => e.ThreadId))
        {
            var list = grp.OrderBy(e => e.DateSent).ToList();
            BuildRelationsAndAttachOrphans(list);
            var roots = list.Where(e => e.Parent == null).OrderBy(e => e.DateSent).ToList();
            var display = new List<ThreadNode>();
            foreach (var r in roots) display.AddRange(BuildDisplay(r));
            result[grp.Key] = display;
        }
        return result;
    }

    private static TicketThread BuildThread(List<EmailMessage> emails)
    {
        var sorted = emails.OrderBy(e => e.DateSent).ToList();
        BuildRelationsAndAttachOrphans(sorted);

        var product = Majority(sorted, e => e.Product);
        var enterprise = Majority(sorted, e => e.Enterprise);
        var ticket = sorted.FirstOrDefault(e => !string.IsNullOrEmpty(e.TicketNumber))?.TicketNumber ?? "";

        var roots = sorted.Where(e => e.Parent == null).OrderBy(e => e.DateSent).ToList();
        var displayRoots = new List<ThreadNode>();
        foreach (var r in roots) displayRoots.AddRange(BuildDisplay(r));

        return new TicketThread
        {
            TicketNumber = ticket,
            Product = product,
            Enterprise = enterprise,
            Emails = sorted,
            DisplayRoots = displayRoots
        };
    }

    /// <summary>在组内建立 父子 关系（按 In-Reply-To，其次 References）。</summary>
    private static void BuildRelations(List<EmailMessage> emails)
    {
        var index = new Dictionary<string, EmailMessage>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in emails)
            if (!string.IsNullOrEmpty(e.MessageId) && !index.ContainsKey(e.MessageId))
                index[e.MessageId] = e;

        foreach (var e in emails) { e.Parent = null; e.Children.Clear(); }

        foreach (var e in emails)
        {
            EmailMessage? parent = null;
            if (!string.IsNullOrEmpty(e.InReplyTo) && index.TryGetValue(e.InReplyTo, out var irt))
                parent = irt;
            else
            {
                foreach (var r in SplitRefs(e.References).Reverse())
                    if (index.TryGetValue(r, out var rf)) { parent = rf; break; }
            }
            if (parent != null && !ReferenceEquals(parent, e))
            {
                e.Parent = parent;
                parent.Children.Add(e);
            }
        }
    }

    /// <summary>
    /// 建立父子关系后，把同一工单线程内无法关联的“孤根”挂到最早一封下面，
    /// 保证线程只有一个根（真正的源头），其余邮件正确缩进。
    /// </summary>
    private static void BuildRelationsAndAttachOrphans(List<EmailMessage> emails)
    {
        BuildRelations(emails);
        var sorted = emails.OrderBy(e => e.DateSent).ToList();
        var roots = sorted.Where(e => e.Parent == null).ToList();
        if (roots.Count <= 1) return;
        var main = roots[0];
        foreach (var orphan in roots.Skip(1))
        {
            orphan.Parent = main;
            main.Children.Add(orphan);
        }
    }

    // ===== 展示树（折叠规则）=====

    private static List<ThreadNode> BuildDisplay(EmailMessage root)
    {
        var node = new ThreadNode(root, 0);
        if (root.Children.Count == 1)
            node.Children.AddRange(BuildChain(root.Children[0], 1));
        else
            foreach (var c in root.Children)
                node.Children.AddRange(BuildChain(c, 1));
        return new List<ThreadNode> { node };
    }

    /// <summary>单链折叠：只有 1 个回复时保持同级；≥2 个回复（多人同时回复）则分支加深一层。</summary>
    private static List<ThreadNode> BuildChain(EmailMessage email, int depth)
    {
        var result = new List<ThreadNode>();
        var cur = email;
        while (true)
        {
            var node = new ThreadNode(cur, depth);
            result.Add(node);
            var children = cur.Children.OrderBy(c => c.DateSent).ToList();
            if (children.Count == 0) break;
            if (children.Count == 1)
            {
                cur = children[0]; // 单链 → 同级继续
            }
            else
            {
                foreach (var c in children)
                    node.Children.AddRange(BuildChain(c, depth + 1)); // 多分支 → 加深
                break;
            }
        }
        return result;
    }

    // ===== helpers =====

    private static string? ResolveTicket(EmailMessage e, Dictionary<string, EmailMessage> index)
    {
        var seen = new HashSet<long>();
        var cur = e;
        while (cur != null && seen.Add(cur.Id))
        {
            if (!string.IsNullOrEmpty(cur.TicketNumber)) return cur.TicketNumber;
            EmailMessage? next = null;
            if (!string.IsNullOrEmpty(cur.InReplyTo) && index.TryGetValue(cur.InReplyTo, out var irt))
                next = irt;
            else
            {
                foreach (var r in SplitRefs(cur.References).Reverse())
                    if (index.TryGetValue(r, out var rf)) { next = rf; break; }
            }
            cur = next;
        }
        return null;
    }

    /// <summary>
    /// 无工单号的邮件从其后代回复中继承工单号：报障邮件原文（常在发件箱）没有工单号，
    /// 客服回复时才会把工单号加到标题开头，因此从引用该邮件的后代里取工单号。
    /// </summary>
    private static string? ResolveTicketFromDescendants(
        EmailMessage e, List<EmailMessage> allEmails, Dictionary<string, EmailMessage> index)
    {
        if (string.IsNullOrEmpty(e.MessageId)) return null;
        string? best = null;
        DateTimeOffset? bestDate = null;
        foreach (var d in allEmails)
        {
            if (string.IsNullOrEmpty(d.TicketNumber) || ReferenceEquals(d, e)) continue;
            if (!IsDescendantOf(d, e, index)) continue;
            if (bestDate == null || d.DateSent < bestDate)
            {
                bestDate = d.DateSent;
                best = d.TicketNumber;
            }
        }
        return best;
    }

    /// <summary>d 是否以 e 为祖先（沿 InReplyTo/References 向上走能否到达 e）。</summary>
    private static bool IsDescendantOf(EmailMessage d, EmailMessage e, Dictionary<string, EmailMessage> index)
    {
        var seen = new HashSet<long>();
        var cur = d;
        while (cur != null && seen.Add(cur.Id))
        {
            if (ReferenceEquals(cur, e)) return true;
            EmailMessage? next = null;
            if (!string.IsNullOrEmpty(cur.InReplyTo) && index.TryGetValue(cur.InReplyTo, out var irt))
                next = irt;
            else
            {
                foreach (var r in SplitRefs(cur.References).Reverse())
                    if (index.TryGetValue(r, out var rf)) { next = rf; break; }
            }
            cur = next;
        }
        return false;
    }

    private static IEnumerable<string> SplitRefs(string references) =>
        references.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Majority(List<EmailMessage> emails, Func<EmailMessage, string> selector)
        => emails.Where(e => !string.IsNullOrEmpty(selector(e)))
                 .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(g => g.Count())
                 .FirstOrDefault()?.Key ?? "";
}
