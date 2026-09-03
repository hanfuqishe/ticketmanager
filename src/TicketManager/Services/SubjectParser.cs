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
/// - 方括号里超过 3 个单词的内容（如 "[ EC Feature Request – ... ]"）是标题/故障描述，不是产品名，
///   从中整词拆解产品简称（EC/OPM…）→ 产品，其余 → 故障描述
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

    /// <summary>内置产品简称 → 全称（不区分大小写）。新增/修改产品简称请在「设置 → 产品简称」中配置，无需改代码。</summary>
    private static readonly Dictionary<string, string> DefaultAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ec"] = "Endpoint Central",
        ["opm"] = "OPManager"
    };

    /// <summary>当前生效的简称表 = 内置 + 用户自定义。不可变引用，由 <see cref="SetAliases"/> 整体替换，避免并发读写竞争。</summary>
    private static volatile Dictionary<string, string> _aliases = DefaultAliases;

    /// <summary>注入用户自定义 产品简称→全称 映射（追加/覆盖内置表）。在配置加载/保存后调用。</summary>
    public static void SetAliases(IEnumerable<KeyValuePair<string, string>>? extra)
    {
        if (extra == null) { _aliases = DefaultAliases; return; }
        var merged = new Dictionary<string, string>(DefaultAliases, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in extra)
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                merged[kv.Key.Trim()] = kv.Value.Trim();
        _aliases = merged;
    }

    /// <summary>当前生效的简称表（只读视图，供诊断/展示）。</summary>
    public static IReadOnlyDictionary<string, string> CurrentAliases => _aliases;

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

        // 2. 最多两个 产品/企业 标签（顺序不定，跳过空标签）；
        //    超过 3 个单词的标签（如 "[ EC Feature Request – ... ]"）是标题/故障描述，不作为产品/企业。
        var tags = new List<string>();
        var titles = new List<string>();
        while (tags.Count < 2)
        {
            var g = TagRegex.Match(s);
            if (!g.Success) break;
            var tag = g.Groups["sq"].Success ? g.Groups["sq"].Value.Trim() : g.Groups["cq"].Value.Trim();
            s = s[g.Length..].Trim();
            if (tag.Length == 0) continue;
            if (WordCount(tag) > 3) { titles.Add(tag); continue; }
            tags.Add(tag);
        }

        var (product, enterprise) = AssignTags(tags);

        // 3. 长标题不是产品名：从中整词拆解 产品简称（EC/OPM…）→ 产品，其余 → 故障描述
        var fault = s.Trim();
        if (titles.Count > 0)
        {
            var parts = new List<string>();
            foreach (var t in titles)
            {
                var (p, rest) = ExtractProductFromTitle(t);
                if (p.Length > 0 && product.Length == 0) product = p;
                parts.Add(rest);
            }
            var combined = string.Join(" ", parts).Trim();
            if (combined.Length > 0) fault = combined;
        }

        if (ticket.Length == 0 && tags.Count == 0 && titles.Count == 0) return null;
        return new ParsedSubject(ticket, NormalizeProduct(product), enterprise, fault);
    }

    /// <summary>统计标签中的“单词”数（纯标点符号不计）。</summary>
    private static int WordCount(string s)
    {
        var count = 0;
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!w.All(char.IsPunctuation)) count++;
        return count;
    }

    /// <summary>长标题中整词拆解产品简称（EC/OPM 等）：返回 (产品全称, 去掉简称后的标题)。</summary>
    private static (string Product, string Remainder) ExtractProductFromTitle(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var key = words[i].Trim().TrimEnd('：', ':', '，', ',', '(', ')', '（', '）');
            if (_aliases.TryGetValue(key, out var full))
            {
                var rest = string.Join(" ", words.Where((_, j) => j != i)).Trim();
                return (full, rest);
            }
        }
        return ("", title);
    }

    /// <summary>产品简称规范化：EC→Endpoint Central，OPM→OPManager 等（含设置里自定义的简称）。</summary>
    public static string NormalizeProduct(string product)
    {
        if (string.IsNullOrEmpty(product)) return product;
        return _aliases.TryGetValue(product.Trim(), out var full) ? full : product;
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

    public static bool ContainsCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        return false;
    }
}
