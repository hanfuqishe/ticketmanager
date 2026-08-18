using System.Text.RegularExpressions;

namespace TicketManager.Services;

/// <summary>解析结果。</summary>
public record ParsedSubject(string TicketNumber, string Product, string Enterprise, string Fault);

/// <summary>
/// 解析工单主题：
/// - 工单号：[###工单号###] 或 [## 工单号 ##]（也支持【】包裹）
/// - 产品/企业：[产品][企业] 或 【产品】【企业】，可混用，顺序不定
/// - 自动剥离 Re:/回复:/Fwd:/转发: 前缀
/// - 若产品与企业一个英文一个中文，则英文=产品，中文=企业；同为一种语言则按出现顺序（首个=产品）
/// </summary>
public static class SubjectParser
{
    private static readonly Regex PrefixRegex = new(
        @"^\s*(?:re|fwd|fw|回复|转发|答复|自动回复)\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TicketRegex = new(
        @"^[\[【]#+\s*(?<ticket>[^\]】#]+?)\s*#+[\]】]\s*",
        RegexOptions.Compiled);

    /// <summary>单个标签：[x] 或 【x】。</summary>
    private static readonly Regex TagRegex = new(
        @"^(?:\[(?<sq>[^\]]*)\]|【(?<cq>[^】]*)】)\s*",
        RegexOptions.Compiled);

    /// <summary>产品简称 → 全称（不区分大小写，新增简称在此追加）。</summary>
    private static readonly Dictionary<string, string> ProductAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ec"] = "Endpoint Central",
        ["opm"] = "OPManager"
    };

    public static ParsedSubject? Parse(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var s = subject.Trim();
        while (true)
        {
            var m = PrefixRegex.Match(s);
            if (!m.Success) break;
            s = s[m.Length..].Trim();
        }

        // 1. 工单号（可选）
        var ticket = "";
        var tm = TicketRegex.Match(s);
        if (tm.Success)
        {
            ticket = tm.Groups["ticket"].Value.Trim();
            s = s[tm.Length..].Trim();
        }

        // 2. 最多两个 产品/企业 标签（顺序不定，跳过空标签）
        var tags = new List<string>();
        while (tags.Count < 2)
        {
            var g = TagRegex.Match(s);
            if (!g.Success) break;
            var tag = g.Groups["sq"].Success ? g.Groups["sq"].Value.Trim() : g.Groups["cq"].Value.Trim();
            s = s[g.Length..].Trim();
            if (tag.Length > 0) tags.Add(tag);
        }

        if (ticket.Length == 0 && tags.Count == 0) return null;

        var (product, enterprise) = AssignTags(tags);
        return new ParsedSubject(ticket, NormalizeProduct(product), enterprise, s.Trim());
    }

    /// <summary>产品简称规范化：EC→Endpoint Central，OPM→OPManager 等。</summary>
    private static string NormalizeProduct(string product)
    {
        if (string.IsNullOrEmpty(product)) return product;
        return ProductAliases.TryGetValue(product.Trim(), out var full) ? full : product;
    }

    /// <summary>把标签分给 产品/企业：一英一中时英文=产品、中文=企业；同语言按出现顺序。</summary>
    private static (string Product, string Enterprise) AssignTags(List<string> tags)
    {
        if (tags.Count == 0) return ("", "");
        if (tags.Count == 1)
            return ContainsCjk(tags[0]) ? ("", tags[0]) : (tags[0], "");

        var a = tags[0];
        var b = tags[1];
        bool aCjk = ContainsCjk(a), bCjk = ContainsCjk(b);
        if (aCjk != bCjk)
            return aCjk ? (b, a) : (a, b); // 一英一中：英文=产品，中文=企业
        return (a, b);                      // 同语言：首个=产品，次个=企业
    }

    private static bool ContainsCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        return false;
    }
}
