using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TicketManager.Models;

namespace TicketManager.Services;

public record ZohoFolder(long Id, string Name, string DisplayName);

public record ZohoMessageSummary(
    long MessageId, long ThreadId, long FolderId, string Subject,
    string FromAddress, string ToAddress, string CcAddress, string SentDate, string ReceivedTime);

/// <summary>
/// Zoho Mail REST API 客户端（美国数据中心）。IMAP 被封锁后替代 MailKit 拉取邮件。
/// 认证：OAuth 2.0（client id/secret + refresh token 换取并自动刷新 access token），
/// 请求头使用 Zoho-oauthtoken。详见 docs/zoho-rest-api.md。
/// </summary>
public class ZohoMailApiService
{
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private string? _accessToken;
    private DateTime _accessTokenExpiry = DateTime.MinValue;

    public ZohoMailApiService(AppConfig config)
    {
        _config = config;
        _http = CreateHttpClient();
    }

    /// <summary>总开关“启用代理”且勾选“用于 Zoho REST API”且配置了代理地址时，经 代理（Socks4/Socks5/Http）访问 Zoho。
    /// 用 .NET 原生代理（WebProxy）而非自定义 ConnectCallback：原生代理的连接/握手可靠响应取消令牌与 ConnectTimeout，
    /// 保证“停止同步”能立即中断下载（自定义 ConnectCallback + MailKit 握手不响应取消令牌，会导致下载阶段卡死）。</summary>
    private HttpClient CreateHttpClient()
    {
        if (!_config.UseProxy || !_config.ProxyForZoho || string.IsNullOrEmpty(_config.ProxyHost))
            return new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var scheme = _config.ProxyType switch
        {
            "Socks4" => "socks4",
            "Http" => "http",
            _ => "socks5"
        };
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{scheme}://{_config.ProxyHost}:{_config.ProxyPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(15), // 连接/握手 15 秒超时（原生代理生效，且随请求取消立即中断）
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string ApiBase => string.IsNullOrWhiteSpace(_config.ZohoApiBase)
        ? "https://mail.zoho.com/api"
        : _config.ZohoApiBase.TrimEnd('/');

    /// <summary>认证服务器地址（随数据中心，如 mail.zoho.com → accounts.zoho.com，不含 /api 后缀）。</summary>
    private string AccountsUrl
    {
        get
        {
            var b = ApiBase;
            if (b.Contains("mail.zoho."))
            {
                var accounts = b.Replace("mail.zoho.", "accounts.zoho.");
                return accounts.EndsWith("/api", StringComparison.Ordinal) ? accounts[..^4] : accounts;
            }
            return "https://accounts.zoho.com";
        }
    }

    public bool Configured =>
        !string.IsNullOrEmpty(_config.ZohoClientId) &&
        !string.IsNullOrEmpty(_config.ZohoClientSecret) &&
        !string.IsNullOrEmpty(_config.ZohoRefreshToken);

    /// <summary>Zoho sentDateInGMT 实际是“账号时区墙钟”，与真实 UTC（receivedTime）相差固定毫秒数；此为推导出的偏移。</summary>
    private long _sentOffsetMs;

    /// <summary>
    /// 推导发送时间偏移：拉取最近一批邮件，取 min(sentDateInGMT - receivedTime)。
    /// receivedTime 是真实 UTC，sentDateInGMT 是账号时区墙钟；自动/快速回复差最小，最接近真实偏移。
    /// 不依赖不可靠的 timeZone 字段（实测其值为 IANA 名如 Asia/Shanghai，与 sentDateInGMT 实际偏移不符）。
    /// </summary>
    public async Task LoadAccountTimeZoneAsync(long accountId, CancellationToken ct = default)
    {
        try
        {
            var folders = await GetFoldersAsync(accountId, ct);
            var inbox = folders.FirstOrDefault(f => f.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase));
            if (inbox == null) return;
            var list = await ListMessagesAsync(accountId, inbox.Id, 1, 30, ct);
            long minDiff = long.MaxValue;
            foreach (var m in list)
            {
                if (long.TryParse(m.SentDate, out var s) && long.TryParse(m.ReceivedTime, out var r) && s >= r)
                    minDiff = Math.Min(minDiff, s - r);
            }
            if (minDiff != long.MaxValue) _sentOffsetMs = minDiff;
        }
        catch { /* 获取失败则不修正 */ }
    }

    /// <summary>获取（必要时用 refresh token 刷新）Access Token；失败返回 null。</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiry.AddMinutes(-2))
            return _accessToken;

        var url = $"{AccountsUrl}/oauth/v2/token?" +
                  $"refresh_token={Uri.EscapeDataString(_config.ZohoRefreshToken)}" +
                  $"&grant_type=refresh_token" +
                  $"&client_id={Uri.EscapeDataString(_config.ZohoClientId)}" +
                  $"&client_secret={Uri.EscapeDataString(_config.ZohoClientSecret)}";
        using var resp = await _http.PostAsync(url, null, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("access_token", out var t)) return null;
        _accessToken = t.GetString();
        var exp = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var v) ? v : 3600;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(exp);
        return _accessToken;
    }

    /// <summary>发起带认证的 GET，返回响应 data 节点；失败返回 null。</summary>
    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;
        var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    /// <summary>获取账号列表，返回第一个账号的 accountId（配置了 Account ID 则直接用）。</summary>
    public async Task<long?> GetAccountIdAsync(CancellationToken ct = default)
    {
        if (long.TryParse(_config.ZohoAccountId, out var configured)) return configured;
        var data = await GetJsonAsync("/accounts", ct);
        if (data == null || data.Value.ValueKind != JsonValueKind.Array) return null;
        foreach (var acc in data.Value.EnumerateArray())
        {
            var v = GetPropLong(acc, "accountId");
            if (v != 0) return v;
        }
        return null;
    }

    /// <summary>获取某账号的全部文件夹（含 INBOX、已发送等）。</summary>
    public async Task<List<ZohoFolder>> GetFoldersAsync(long accountId, CancellationToken ct = default)
    {
        var result = new List<ZohoFolder>();
        var data = await GetJsonAsync($"/accounts/{accountId}/folders", ct);
        if (data == null || data.Value.ValueKind != JsonValueKind.Array) return result;
        foreach (var f in data.Value.EnumerateArray())
        {
            var fid = GetPropLong(f, "folderId");
            if (fid == 0) continue;
            var name = GetPropStr(f, "folderName");
            result.Add(new ZohoFolder(fid, name, name));
        }
        return result;
    }

    /// <summary>列出某文件夹的一页邮件（按日期倒序，limit 最大 200）。</summary>
    public async Task<List<ZohoMessageSummary>> ListMessagesAsync(
        long accountId, long folderId, int start, int limit, CancellationToken ct = default)
    {
        var result = new List<ZohoMessageSummary>();
        var data = await GetJsonAsync(
            $"/accounts/{accountId}/messages/view?folderId={folderId}&start={start}&limit={limit}" +
            $"&sortBy=date&sortorder=false&includeto=true", ct);
        if (data == null || data.Value.ValueKind != JsonValueKind.Array) return result;
        foreach (var m in data.Value.EnumerateArray())
        {
            result.Add(new ZohoMessageSummary(
                GetPropLong(m, "messageId"), GetPropLong(m, "threadId"), GetPropLong(m, "folderId"),
                GetPropStr(m, "subject"), GetPropStr(m, "fromAddress"), GetPropStr(m, "toAddress"),
                GetPropStr(m, "ccAddress"), GetPropStr(m, "sentDateInGMT"), GetPropStr(m, "receivedTime")));
        }
        return result;
    }

    /// <summary>取单封邮件的完整内容（含正文与头信息），返回 data 节点；失败返回 null。</summary>
    public async Task<JsonElement?> GetMessageContentAsync(
        long accountId, long folderId, long messageId, CancellationToken ct = default)
    {
        return await GetJsonAsync(
            $"/accounts/{accountId}/folders/{folderId}/messages/{messageId}/content?includeBlockContent=true", ct);
    }

    /// <summary>原始 GET（返回完整响应 JSON 字符串），供诊断/取 headers 等使用；失败返回 null。</summary>
    public async Task<string?> GetJsonRawAsync(string path, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;
        var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>把 列表摘要 + 内容 组装成 EmailMessage（内容获取失败返回 null）。</summary>
    public async Task<EmailMessage?> ToEmailMessageAsync(
        long accountId, long folderId, ZohoMessageSummary m, string folderName, CancellationToken ct = default)
    {
        var content = await GetMessageContentAsync(accountId, folderId, m.MessageId, ct);
        var body = "";
        if (content != null && content.Value.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            body = StripHtml(c.GetString() ?? "");
        var maxChars = _config.MaxBodyChars;
        if (body.Length > maxChars) body = body[..maxChars];
        return new EmailMessage
        {
            Folder = folderName,
            Uid = 0,
            MessageId = "",
            ZohoMessageId = m.MessageId.ToString(),
            ZohoThreadId = m.ThreadId,
            FromAddress = CleanSingleAddress(m.FromAddress),
            FromName = "",
            ToAddresses = ExtractAddresses(m.ToAddress),
            CcAddresses = ExtractAddresses(m.CcAddress),
            Subject = m.Subject,
            DateSent = MsToSentDateTime(m.SentDate),
            DateReceived = MsToDateTime(m.ReceivedTime),
            BodyText = body,
            ContentHash = ComputeHash(body)
        };
    }

    /// <summary>从地址串中提取所有邮箱地址，用 ; 连接（Zoho 的 to/cc 常带姓名与 HTML 编码）。</summary>
    private static string ExtractAddresses(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Equals("Not Provided", StringComparison.OrdinalIgnoreCase)) return "";
        s = System.Net.WebUtility.HtmlDecode(s);
        var matches = Regex.Matches(s, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}");
        return string.Join(";", matches.Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string CleanSingleAddress(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var first = ExtractAddresses(s);
        return first.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static DateTimeOffset MsToDateTime(string ms)
        => long.TryParse(ms, out var v)
            ? DateTimeOffset.FromUnixTimeMilliseconds(v).ToLocalTime() // receivedTime 为真实 UTC，转本地时区（如北京时间）
            : DateTimeOffset.MinValue;

    /// <summary>sentDateInGMT 是“账号时区墙钟”：墙钟 - 推导偏移 = 真实 UTC，再转本地时区。</summary>
    private DateTimeOffset MsToSentDateTime(string ms)
    {
        if (!long.TryParse(ms, out var v)) return DateTimeOffset.MinValue;
        return DateTimeOffset.FromUnixTimeMilliseconds(v - _sentOffsetMs).ToLocalTime();
    }

    /// <summary>HTML 正文转纯文本（与 IMAP 路径保持一致的处理）。</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = Regex.Replace(html, @"(?s)<(script|style)[^>]*>.*?</\1>", " ");
        html = Regex.Replace(html, @"(?i)</?(p|div|br|li|tr|h[1-6])[^>]*>", "\n");
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    private static string ComputeHash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
    }

    /// <summary>读取属性为 long：兼容数字或字符串（Zoho 部分字段返回字符串）。</summary>
    private static long GetPropLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var v2)) return v2;
        return 0;
    }

    private static string GetPropStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
