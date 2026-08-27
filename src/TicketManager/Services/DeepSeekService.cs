using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// DeepSeek API 服务（OpenAI 兼容接口）。
/// - 为每封邮件生成一句话标题
/// - 为每个工单智能总结状态
/// 内部用信号量限制并发请求数。
/// </summary>
public class DeepSeekService : IDisposable
{
    /// <summary>日志上传确认通知邮箱：收到它说明我方刚把日志文件上传给研发，球在研发一侧（等待研发回复）。</summary>
    private const string LogUploadNotifier = "bonitas@notification.zohocorpsite.com";

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(4, 4); // AI 并发上限（标题/状态/元数据分析并行用；太高易触发 DeepSeek 限流）

    public DeepSeekService(AppConfig config)
    {
        _config = config;
        var handler = new HttpClientHandler();
        if (config.UseProxy && config.ProxyForDeepSeek && !string.IsNullOrEmpty(config.ProxyHost))
        {
            handler.Proxy = new WebProxy($"{config.ProxyHost}:{config.ProxyPort}");
            handler.UseProxy = true;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrEmpty(config.DeepSeekApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DeepSeekApiKey);
    }

    public bool Configured => !string.IsNullOrEmpty(_config.DeepSeekApiKey);

    /// <summary>为单封邮件生成一句话标题。失败返回 null。</summary>
    public async Task<string?> SummarizeTitleAsync(EmailMessage email)
    {
        if (!Configured) return null;
        const string systemPrompt =
            "你是一名 IT 技术支持工程师。请阅读下面这封邮件，用一句简洁的中文（不超过 30 字）概括邮件的核心内容，作为该邮件的标题。" +
            "只输出标题本身，不要引号、不要前缀、不要任何多余说明。";

        var userContent = new StringBuilder();
        userContent.AppendLine($"【发件人】{email.DisplaySender}");
        userContent.AppendLine($"【时间】{email.DateSent:yyyy-MM-dd HH:mm}");
        userContent.AppendLine($"【原始主题】{email.Subject}");
        userContent.AppendLine("【正文】");
        userContent.AppendLine(Truncate(email.BodyText, _config.MaxBodyChars));

        return await ChatAsync(systemPrompt, userContent.ToString(), maxTokens: 64);
    }

    /// <summary>
    /// 为整个工单生成状态总结。失败返回 null。
    /// 可传 focusEmails（只提交这些新增邮件）与 previousSummary（上次总结）做增量更新，
    /// 避免把同线索中未变化的其他邮件重复提交给 AI。
    /// </summary>
    public async Task<(string Status, string Summary)?> SummarizeThreadAsync(
        TicketThread thread,
        IReadOnlyList<EmailMessage>? focusEmails = null,
        string? previousSummary = null)
    {
        if (!Configured) return null;
        const string systemPrompt =
            "你是一名 IT 技术支持工程师。以下是某个工单的完整邮件往来记录。" +
            "请判断该工单当前的进展状态，并用一句话简要说明。" +
            "严格按以下格式输出，不要多余内容：\n" +
            "状态：<新建|处理中|等待客户回复|等待客服回复|等待研发回复|纳入开发计划|合并或拆分为其他工单|已解决|需升级>\n" +
            "总结：<一句话>\n" +
            "邮件往来中每封已标注方向：[我]=客服本人发出的邮件，[客服]=技术支持/厂商客服发出的邮件，[客户]=客户发出的邮件。" +
            "总结时必须严格区分客户与客服的意见：客户提出的需求、问题、不满、反馈应归为客户的意见；" +
            "[我]/[客服] 发出的内容是客服的处理过程、判断与建议，属于我方/客服方，不要把这些说成是客户的意见。" +
            "方向判断（决定状态的核心）：状态表示『当前卡在哪、要不要催』，务必结合最后一封邮件的发件人与内容判断，而不是只看问题本身。" +
            "三个状态的区别（重点，务必遵守）：" +
            "- 「处理中」：厂商客服/技术支持已明确表态在处理——如 We will check with our team and update you / We are working on it / Noted, will update you / 正在跟进、正在排查。已受理、正在推进，暂时不用催。" +
            "- 「等待客服回复」：轮到厂商客服/技术支持回应，但对方尚未表态（客户刚提问/催进度、需求刚转达，客服还没有任何“会处理”的确认）→ 需要去催厂商客服。" +
            "- 「等待客户回复」：轮到客户回应——客服向客户提问、要求提供信息/确认/复现，或客服已回复完毕在等客户确认。" +
            "判定要领：" +
            "① 看最后一封邮件的发件人：" +
            "   - 最后是客户发的（提问/上传/催进度）→ 轮到客服回复 → 「等待客服回复」或「处理中」。" +
            "   - 最后是客服发的，且在向客户提问/要信息 → 「等待客户回复」；最后是客服发的但表态在处理 → 「处理中」。" +
            "   - 最后是我方（[我]）发的（如替客户回答客服的提问、把客户需求转达给客服）→ 球已回到客服这边 → 「等待客服回复」或「处理中」，绝不能判「等待客户回复」。" +
            "② 「等待客户回复」的前提是对话中确实有客户在参与（存在 [客户] 角色的邮件）。" +
            "   若整个线索只有 [我] 和 [客服]、客户从未发过邮件——即使客服问了关于客户的问题，也应视为我方已替客户答复、等客服继续，用「等待客服回复」或「处理中」，不要用「等待客户回复」。" +
            "③ 厂商客服一旦说过“会跟进/正在处理”，就用「处理中」，不要再写「等待客服回复」；绝不能把“客服在跟进”写成「等待客户回复」。" +
            "④ 特殊通知：标 [上传确认] 的邮件来自日志上传确认通知（bonitas@notification.zohocorpsite.com），说明我方刚把日志文件上传给研发。" +
            "   若线索最后一封是它 → 球在研发一侧 → 用「等待研发回复」；若其后研发已回复，则按正常方向判断。";

        var userContent = new StringBuilder();
        userContent.AppendLine($"【工单号】{thread.TicketNumber}  产品：{thread.Product}  客户：{thread.Enterprise}");
        if (!string.IsNullOrEmpty(previousSummary))
            userContent.AppendLine($"【上次总结】{previousSummary}");
        userContent.AppendLine(focusEmails == null ? "【邮件往来】" : "【本次新增邮件】");
        var emails = focusEmails ?? thread.Emails;
        foreach (var e in emails.OrderBy(e => e.DateSent))
        {
            userContent.AppendLine($"---- {e.DateSent:MM-dd HH:mm} {DirectionLabel(e.FromAddress)}{e.DisplaySender} ----");
            userContent.AppendLine(Truncate(e.BodyText, _config.MaxBodyChars / 4));
        }

        var resp = await ChatAsync(systemPrompt, userContent.ToString(), maxTokens: 256);
        if (string.IsNullOrWhiteSpace(resp)) return null;

        var status = "";
        var summary = "";
        foreach (var line in resp.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("状态：")) status = t["状态：".Length..].Trim();
            else if (t.StartsWith("总结：")) summary = t["总结：".Length..].Trim();
        }
        if (string.IsNullOrEmpty(status))
        {
            // 响应无法解析出状态行：记录原始响应便于排查
            App.Log("DeepSeek.ParseStatus", new Exception("无法从响应解析出『状态：』行，原始响应: " + (resp.Length > 400 ? resp[..400] : resp)));
            return null;
        }

        // 日志上传确认：若线索最后一封来自上传确认通知，说明刚把日志交给研发，球在研发一侧（硬性判定，不受 AI 波动影响）
        var lastEmail = thread.Emails.OrderBy(e => e.DateSent).LastOrDefault();
        if (lastEmail != null &&
            string.Equals(lastEmail.FromAddress, LogUploadNotifier, StringComparison.OrdinalIgnoreCase))
            status = "等待研发回复";

        return (status, string.IsNullOrEmpty(summary) ? resp.Trim() : summary);
    }

    /// <summary>按发件人地址标注邮件方向：自己的邮箱或我方同事域名→[我]，关注客服邮箱→[客服]，日志上传确认→[上传确认]，否则→[客户]。</summary>
    private string DirectionLabel(string fromAddress)
    {
        if (string.IsNullOrEmpty(fromAddress)) return "[客户]";
        if (string.Equals(fromAddress, _config.ImapUsername, StringComparison.OrdinalIgnoreCase) ||
            IsMySupportDomain(fromAddress)) // 我方支持人员（同事）域名 → [我]
            return "[我]";
        // 日志上传确认通知：我方上传日志给研发的确认，非客户也非客服
        if (string.Equals(fromAddress, LogUploadNotifier, StringComparison.OrdinalIgnoreCase))
            return "[上传确认]";
        // 厂商客服邮箱（support@manageengine/zohocorp）即使未被关注也算客服，避免被误标为客户
        if (_config.MonitoredAddresses.Any(m => string.Equals(fromAddress, m, StringComparison.OrdinalIgnoreCase)) ||
            IsSupportMailbox(fromAddress))
            return "[客服]";
        return "[客户]";
    }

    /// <summary>邮箱 @ 后缀是否命中「我方支持人员域名」（精确或子域匹配），如 manageengine.cn / zohomail.com。</summary>
    private bool IsMySupportDomain(string fromAddress)
    {
        if (_config.MySupportDomains.Count == 0) return false;
        var at = fromAddress.LastIndexOf('@');
        if (at < 0 || at == fromAddress.Length - 1) return false;
        var domain = fromAddress[(at + 1)..].Trim();
        foreach (var d in _config.MySupportDomains)
        {
            var dd = d.Trim();
            if (dd.Length == 0) continue;
            if (string.Equals(dd, domain, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith("." + dd, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>@ 前含 support、@ 后含 manageengine 或 zohocorp 的厂商客服邮箱（与自动关注规则一致）。</summary>
    private static bool IsSupportMailbox(string addr)
    {
        var at = addr.IndexOf('@');
        if (at <= 0 || at == addr.Length - 1) return false;
        var domain = addr[(at + 1)..];
        return addr[..at].Contains("support", StringComparison.OrdinalIgnoreCase) &&
               (domain.Contains("manageengine", StringComparison.OrdinalIgnoreCase) ||
                domain.Contains("zohocorp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 主题未按约定标注 产品/客户 时，用 AI 从主题与邮箱地址中分析提取。
    /// 返回 (产品, 客户)，无法确定则为空串；两者都空返回 null。
    /// </summary>
    public async Task<(string Product, string Enterprise)?> ExtractMetaAsync(EmailMessage email)
    {
        if (!Configured) return null;
        const string systemPrompt =
            "你是一名 IT 技术支持工程师。下面是一封工单相关邮件的主题与邮箱地址。" +
            "请判断这封邮件涉及的产品名称和客户企业名称。\n" +
            "依据：产品名称从主题中找（可能是简称，如 EC=Endpoint Central、OPM=OPManager）；" +
            "企业名称从发件人/收件人/抄送人的邮箱域名推断（如 @want-want.com 通常对应某企业），可结合常见企业知识。" +
            "注意：域名中含 zoho 或 manageengine 的是本产品厂家（Zoho/ManageEngine）人员，不是客户企业，推断时请排除；" +
            "其他邮箱域名才是客户。" +
            "无法确定就留空，不要编造。\n" +
            "严格只输出两行：\n产品：<名称，无法确定则留空>\n企业：<名称，无法确定则留空>";

        var userContent = new StringBuilder();
        userContent.AppendLine($"【主题】{email.Subject}");
        userContent.AppendLine($"【发件人】{email.FromAddress}");
        userContent.AppendLine($"【收件人】{email.ToAddresses}");
        userContent.AppendLine($"【抄送】{email.CcAddresses}");

        var resp = await ChatAsync(systemPrompt, userContent.ToString(), maxTokens: 100);
        if (string.IsNullOrWhiteSpace(resp)) return null;

        string product = "", enterprise = "";
        foreach (var line in resp.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("产品：")) product = t["产品：".Length..].Trim();
            else if (t.StartsWith("企业：")) enterprise = t["企业：".Length..].Trim();
        }
        if (string.IsNullOrEmpty(product) && string.IsNullOrEmpty(enterprise)) return null;
        return (SubjectParser.NormalizeProduct(product), enterprise);
    }

    /// <summary>把英文邮件正文翻译成简体中文。失败返回 null。</summary>
    public async Task<string?> TranslateTextAsync(string text)
    {
        if (!Configured || string.IsNullOrWhiteSpace(text)) return null;
        const string systemPrompt =
            "你是一名专业的中英技术翻译。请把下面的英文邮件正文翻译成简体中文。" +
            "保留原文的分段与换行结构，不要添加任何解释、前缀或标注。只输出译文。";
        var userContent = Truncate(text, _config.MaxBodyChars);
        return await ChatAsync(systemPrompt, userContent, maxTokens: 1024);
    }

    /// <summary>把中文/混合文本翻译成英文（回复邮件用）。失败返回 null。</summary>
    public async Task<string?> TranslateToEnglishAsync(string text)
    {
        if (!Configured || string.IsNullOrWhiteSpace(text)) return null;
        const string systemPrompt =
            "你是一名专业的中英技术翻译。请把下面的文本翻译成英文，用于回复技术支持邮件。" +
            "保留分段与换行结构，语气专业礼貌，不要添加任何解释、前缀或标注。只输出译文。";
        var userContent = Truncate(text, _config.MaxBodyChars);
        return await ChatAsync(systemPrompt, userContent, maxTokens: 1024);
    }

    /// <summary>根据当前企业名（可能是简称/拼音/英文）及相关的邮箱域名，用 AI 推断可能对应的正式中文名称列表；失败返回 null。</summary>
    public async Task<List<string>?> SuggestEnterpriseNamesAsync(string enterpriseName, IReadOnlyList<string>? relatedDomains = null)
    {
        if (!Configured || string.IsNullOrWhiteSpace(enterpriseName)) return null;
        const string systemPrompt =
            "你熟悉国内外各企业、公司的正式注册名称与常用称谓。" +
            "用户会提供一个企业名称（可能是简称、拼音、英文名或口语叫法），并可能附带该企业相关的邮箱域名。" +
            "请结合域名与名称，推断它最可能对应的几个正式中文名称。只输出名称，每行一个，不要编号、不要引号、不要任何解释。" +
            "最多 5 个；如果无法确定就输出最接近的 1-2 个。";
        var user = $"当前企业名称：{enterpriseName}";
        if (relatedDomains is { Count: > 0 })
            user += "\n该企业相关的邮箱域名（可辅助判断）：" + string.Join("、", relatedDomains);
        var resp = await ChatAsync(systemPrompt, user, maxTokens: 128);
        if (string.IsNullOrWhiteSpace(resp)) return null;
        var list = resp.Split('\n')
            .Select(l => l.Trim().Trim('、', '，', ',', '.', '。', ' ', '\t', '"', '“', '”', '[', ']', '1', '2', '3', '4', '5'))
            .Where(l => l.Length > 0)
            .Take(5)
            .ToList();
        return list.Count > 0 ? list : null;
    }

    private async Task<string?> ChatAsync(string systemPrompt, string userContent, int maxTokens)
    {
        if (!Configured) return null;
        await _gate.WaitAsync();
        try
        {
            var payload = new
            {
                model = _config.DeepSeekModel,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                },
                temperature = 0.3,
                max_tokens = maxTokens,
                stream = false
            };
            var url = _config.DeepSeekBaseUrl.TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var content = choices[0].GetProperty("message").GetProperty("content").GetString();
                return content?.Trim();
            }
            App.Log("DeepSeek.Chat", new Exception("响应缺少 choices，原始响应: " + (json.Length > 400 ? json[..400] : json)));
            return null;
        }
        catch (Exception ex)
        {
            App.Log("DeepSeek.Chat", ex); // 记录失败原因（网络/限流/超时等），便于排查
            return null; // 网络/限流/解析失败统一返回 null，由上层计数
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r\n", "\n").Trim();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n…（已截断）";
    }

    public void Dispose() => _http.Dispose();
}
