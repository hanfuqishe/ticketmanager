using System.Text.RegularExpressions;
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

        // 第一步：为每封邮件确定所属工单（含 References/后代继承、英文自动回复主题 Ticket ID）
        var ticketOf = new Dictionary<long, string>();
        foreach (var e in allEmails)
        {
            var ticket = !string.IsNullOrEmpty(e.TicketNumber)
                ? e.TicketNumber
                : ParseAckTicketId(e.Subject) ?? ResolveTicket(e, index) ?? ResolveTicketFromDescendants(e, allEmails, index);
            ticketOf[e.Id] = ticket ?? "";
        }

        // 第二步：用 Zoho threadId 把 无工单号的报障邮件 并进 客服回复所在工单组。
        // REST 拉取的邮件没有 MessageId/In-Reply-To/References（全空），
        // 报障邮件（发件箱）无工单号无法靠引用继承，但 Zoho 官方 threadId 天然标识同一对话。
        var threadToTicket = new Dictionary<long, string>();
        var threadCounts = new Dictionary<long, Dictionary<string, int>>(/* ticket 计数 */);
        foreach (var e in allEmails)
        {
            if (e.ZohoThreadId is not long zid || zid <= 0) continue;
            var t = ticketOf[e.Id];
            if (string.IsNullOrEmpty(t)) continue;
            if (!threadCounts.TryGetValue(zid, out var counts))
            {
                counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                threadCounts[zid] = counts;
            }
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        foreach (var kv in threadCounts)
            threadToTicket[kv.Key] = kv.Value.OrderByDescending(x => x.Value).First().Key;
        foreach (var e in allEmails)
        {
            if (!string.IsNullOrEmpty(ticketOf[e.Id])) continue;
            if (e.ZohoThreadId is long zid && zid > 0 && threadToTicket.TryGetValue(zid, out var t))
                ticketOf[e.Id] = t;
        }

        // 第三步：自动回复正文登记的原报障主题 → 关联无工单号的报障邮件。
        // Zoho 自动回复（“我们已收到您的工单”）正文含 “我们已经为您登记了工单：”原主题“， ID为[工单号]”；
        // 报障邮件（发件箱）主题与该原主题一致但无工单号，据此把报障邮件并入自动回复所在工单。
        var autoReplyTickets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 归一化主题 -> 工单号
        foreach (var e in allEmails)
        {
            if (string.IsNullOrEmpty(ticketOf[e.Id])) continue;
            if (string.IsNullOrEmpty(e.BodyText)) continue;
            var reported = ExtractReportedSubject(e.BodyText);
            if (string.IsNullOrEmpty(reported)) continue;
            autoReplyTickets[NormalizeSubject(reported)] = ticketOf[e.Id];
        }
        if (autoReplyTickets.Count > 0)
        {
            foreach (var e in allEmails)
            {
                if (!string.IsNullOrEmpty(ticketOf[e.Id])) continue;
                if (autoReplyTickets.TryGetValue(NormalizeSubject(e.Subject), out var t))
                    ticketOf[e.Id] = t;
            }
        }

        // 第三步(补充)：报障主题匹配 —— 客服回复去掉 Re:/工单号标记后即回到原始报障主题；
        // 发件箱的报障邮件（无工单号，Zoho REST 下还可能拿不到 threadId/引用头）据此并入对应工单。
        // 例：回复主题 Re:[## 13801919 ##] [OPManager][施乐百]Privileges… → 基础主题 [OPManager][施乐百]Privileges…，
        //     与发件箱报障邮件主题一致 → 该报障邮件并入工单 13801919 成为根。
        var baseSubjectTickets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEmails)
        {
            if (string.IsNullOrEmpty(ticketOf[e.Id])) continue;
            var bs = BaseSubject(e.Subject);
            if (string.IsNullOrEmpty(bs) || baseSubjectTickets.ContainsKey(bs)) continue;
            baseSubjectTickets[bs] = ticketOf[e.Id];
        }
        if (baseSubjectTickets.Count > 0)
        {
            foreach (var e in allEmails)
            {
                if (!string.IsNullOrEmpty(ticketOf[e.Id])) continue;
                if (baseSubjectTickets.TryGetValue(BaseSubject(e.Subject), out var t))
                    ticketOf[e.Id] = t;
            }
        }

        // 第四步：按工单号分组；无工单号的先按 Zoho threadId 归并（同一会话同一线索），
        // 都没有的按引用链归属同一对话（同对话多封无主邮件并一组），否则各自独立成线索。
        // 不能再用 企业|产品 兜底——那会把同一企业同一产品下的多条无关报障堆进同一条大杂烩线索
        // （例：旺旺集团/Endpoint Central 下 2025、2026-05、2026-08 三条无关报障被并成一条）。
        var groups = new Dictionary<string, List<EmailMessage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in allEmails)
        {
            var ticket = ticketOf[e.Id];
            var key = string.IsNullOrEmpty(ticket)
                ? e.ZohoThreadId is long zid && zid > 0
                    ? $"__zthread__{zid}"
                    : UntitledGroupKey(e, index)
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

        // 根邮件（真正源头）的产品/客户优先作为整棵线程的产品/客户；空缺时回退多数投票
        var root = sorted.FirstOrDefault(e => e.Parent == null) ?? sorted.FirstOrDefault();
        var product = root != null && !string.IsNullOrEmpty(root.Product)
            ? root.Product
            : Majority(sorted, e => e.Product);
        var enterprise = root != null && !string.IsNullOrEmpty(root.Enterprise)
            ? root.Enterprise
            : Majority(sorted, e => e.Enterprise);
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
    /// 保证线程只有一个根（真正的源头），其余邮件正确缩进；并把各节点子节点按时间升序重排。
    /// </summary>
    private static void BuildRelationsAndAttachOrphans(List<EmailMessage> emails)
    {
        BuildRelations(emails);
        var sorted = emails.OrderBy(e => e.DateSent).ToList();
        var roots = sorted.Where(e => e.Parent == null).ToList();
        if (roots.Count > 1)
        {
            var main = roots[0];
            foreach (var orphan in roots.Skip(1))
            {
                orphan.Parent = main;
                main.Children.Add(orphan);
            }
        }
        // 每个节点的子节点按时间升序（挂载孤根后可能打乱顺序）
        foreach (var e in emails)
            e.Children = e.Children.OrderBy(c => c.DateSent).ToList();
    }

    // ===== 展示树（折叠规则）=====

    private static List<ThreadNode> BuildDisplay(EmailMessage root)
    {
        var node = new ThreadNode(root, 0);
        if (root.Children.Count == 1)
            node.Children.AddRange(BuildChain(root.Children[0], 1));
        else
            foreach (var c in root.Children.OrderBy(x => x.DateSent))
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

    /// <summary>无主邮件（无工单号、无 Zoho threadId）的分组键：沿 References/In-Reply-To 追溯到对话链头，
    /// 同一对话的邮件并一组，无引用的各自独立成线索（避免同企业同产品多条无关报障堆成一条大杂烩）。</summary>
    private static string UntitledGroupKey(EmailMessage e, Dictionary<string, EmailMessage> index)
    {
        var cur = e;
        for (var guard = 0; guard < 50; guard++)
        {
            EmailMessage? parent = null;
            if (!string.IsNullOrEmpty(cur.InReplyTo) && index.TryGetValue(cur.InReplyTo, out var irt))
                parent = irt;
            else
            {
                foreach (var r in SplitRefs(cur.References).Reverse())
                    if (index.TryGetValue(r, out var rf)) { parent = rf; break; }
            }
            if (parent == null || ReferenceEquals(parent, cur)) break;
            cur = parent;
        }
        return $"__untitled__|{cur.Id}";
    }

    /// <summary>从 Zoho 自动回复正文提取“登记工单”的原报障主题（中文引号）；无则返回空串。</summary>
    private static string ExtractReportedSubject(string body)
    {
        var m = Regex.Match(body, @"工单[:：]\s*“(?<s>[^”]+)”");
        return m.Success ? m.Groups["s"].Value.Trim() : "";
    }

    /// <summary>从英文自动回复主题提取工单号：Acknowledgement ... – Ticket ID 13411754 → 13411754；无则返回 null。</summary>
    private static string? ParseAckTicketId(string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return null;
        var m = Regex.Match(subject, @"Ticket\s*ID\s*[:#]?\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>主题归一化（HTML 实体解码、压缩空白、去 Re:/回复: 前缀），用于主题匹配。</summary>
    private static string NormalizeSubject(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"^\s*(?:re|回复|答复|fwd|转发)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>主题基础部分：在归一化基础上再去掉工单号标记（如 [## 13801919 ##] / [###T2026-001###]）。
    /// 客服回复（带工单号）去掉 Re: 前缀与工单号标记后即回到原始报障主题。</summary>
    private static string BaseSubject(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var norm = NormalizeSubject(s);
        // 工单号标记：以 # 包裹、内容为字母数字连字符（如 [## 13801919 ##]）；产品/客户中括号不含 #，不会被误删
        norm = Regex.Replace(norm, @"\[#{1,3}\s*[A-Za-z0-9\-]+\s*#{1,3}\]", "", RegexOptions.IgnoreCase);
        return Regex.Replace(norm, @"\s+", " ").Trim();
    }

    private static string Majority(List<EmailMessage> emails, Func<EmailMessage, string> selector)
        => emails.Where(e => !string.IsNullOrEmpty(selector(e)))
                 .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(g => g.Count())
                 .FirstOrDefault()?.Key ?? "";
}
