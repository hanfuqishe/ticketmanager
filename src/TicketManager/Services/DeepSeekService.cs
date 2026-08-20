using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(2, 2);

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

    /// <summary>为整个工单生成状态总结。失败返回 null。</summary>
    public async Task<(string Status, string Summary)?> SummarizeThreadAsync(TicketThread thread)
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
            "方向判断：看最后一封邮件的发件人。若最后一封是客户发的（如客户刚上传日志/提供信息），" +
            "说明轮到客服/技术支持回复，状态应为「等待客服回复」或「处理中」；" +
            "若最后一封是客服/技术支持发的（正在等客户提供信息或确认），才用「等待客户回复」。";

        var userContent = new StringBuilder();
        userContent.AppendLine($"【工单号】{thread.TicketNumber}  产品：{thread.Product}  客户：{thread.Enterprise}");
        userContent.AppendLine("【邮件往来】");
        foreach (var e in thread.Emails.OrderBy(e => e.DateSent))
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
        status = CorrectWaitingDirection(status, thread);
        return (status, string.IsNullOrEmpty(summary) ? resp.Trim() : summary);
    }

    /// <summary>按发件人地址标注邮件方向：自己的邮箱→[我]，关注客服邮箱→[客服]，否则→[客户]。</summary>
    private string DirectionLabel(string fromAddress)
    {
        if (string.IsNullOrEmpty(fromAddress)) return "[客户]";
        if (string.Equals(fromAddress, _config.ImapUsername, StringComparison.OrdinalIgnoreCase))
            return "[我]";
        if (_config.MonitoredAddresses.Any(m => string.Equals(fromAddress, m, StringComparison.OrdinalIgnoreCase)))
            return "[客服]";
        return "[客户]";
    }

    /// <summary>按“最后一封邮件”的发件方向纠正 等待类 状态的归属，避免与总结自相矛盾。</summary>
    private string CorrectWaitingDirection(string status, TicketThread thread)
    {
        if (status is not ("等待客户回复" or "等待客服回复")) return status;
        var last = thread.Emails.OrderBy(e => e.DateSent).LastOrDefault();
        if (last == null || string.IsNullOrEmpty(last.FromAddress)) return status;
        var supportSpokeLast =
            string.Equals(last.FromAddress, _config.ImapUsername, StringComparison.OrdinalIgnoreCase) ||
            _config.MonitoredAddresses.Any(m => string.Equals(last.FromAddress, m, StringComparison.OrdinalIgnoreCase));
        return supportSpokeLast ? "等待客户回复" : "等待客服回复";
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
