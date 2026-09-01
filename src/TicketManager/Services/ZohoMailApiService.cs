using System.IO;
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

/// <summary>上传到 Zoho 文件存储后的附件元数据（发送邮件时在 attachments 数组引用）。</summary>
public record ZohoAttachmentMeta(string StoreName, string AttachmentName, string AttachmentPath);

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

    /// <summary>发送一封邮件（Zoho Mail REST API：POST /accounts/{id}/messages）。
    /// 需要 ZohoMail.messages.CREATE scope。成功返回 (true, null)；失败返回 (false, 错误信息)。
    /// mailFormat：html（默认，支持签名字体/颜色）或 plain；attachmentPaths 非空时先上传附件再发送。</summary>
    public async Task<(bool Success, string? Error)> SendEmailAsync(
        long accountId, string from, string to, string? cc, string subject, string content,
        string mailFormat = "html", CancellationToken ct = default,
        IEnumerable<string>? attachmentPaths = null)
    {
        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return (false, "无法获取 Zoho Access Token，请检查 Zoho REST API 配置。");

        var attachments = attachmentPaths
            ?.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .ToList();
        if (attachments is { Count: > 0 })
        {
            // 带附件：先上传到 Zoho 文件存储拿元数据，再在发信 JSON 的 attachments 数组引用
            // （Zoho 发信接口不接受 multipart 直接带文件，必须先 Upload Attachments API）
            var metas = await UploadAttachmentsAsync(accountId, attachments, ct);
            if (metas.Count != attachments.Count)
                return (false, "附件上传失败，请重试。");

            var attPayload = new Dictionary<string, object?>
            {
                ["fromAddress"] = from,
                ["toAddress"] = to,
                ["subject"] = subject,
                ["content"] = content,
                ["mailFormat"] = mailFormat,
                ["attachments"] = metas.Select(m => new
                {
                    storeName = m.StoreName,
                    attachmentName = m.AttachmentName,
                    attachmentPath = m.AttachmentPath
                }).ToList()
            };
            if (!string.IsNullOrWhiteSpace(cc)) attPayload["ccAddress"] = cc;

            var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/accounts/{accountId}/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(attPayload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
            return await SendAndParseAsync(req, ct);
        }

        var payload = new Dictionary<string, object?>
        {
            ["fromAddress"] = from,
            ["toAddress"] = to,
            ["subject"] = subject,
            ["content"] = content,
            ["mailFormat"] = mailFormat
        };
        if (!string.IsNullOrWhiteSpace(cc)) payload["ccAddress"] = cc;

        var reqJson = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/accounts/{accountId}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        reqJson.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        return await SendAndParseAsync(reqJson, ct);
    }

    /// <summary>执行发送请求并解析 Zoho 响应。成功返回 (true, null)；失败返回 (false, 错误信息)。</summary>
    private async Task<(bool Success, string? Error)> SendAndParseAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("status", out var st) &&
                    st.TryGetProperty("code", out var code) && code.GetInt32() == 200)
                    return (true, null);
                var desc = st.TryGetProperty("description", out var d) ? d.GetString() : "";
                return (false, string.IsNullOrEmpty(desc) ? Truncate(json) : desc);
            }
            catch { return (false, "发送失败（响应解析错误）：" + Truncate(json)); }
        }
        return (false, $"发送失败（HTTP {(int)resp.StatusCode}）：{Truncate(json)}");
    }

    /// <summary>上传附件到 Zoho 文件存储（POST /accounts/{id}/messages/attachments，multipart，字段名 attach）。
    /// 返回每个文件的 {storeName, attachmentName, attachmentPath}；失败返回空列表。</summary>
    public async Task<List<ZohoAttachmentMeta>> UploadAttachmentsAsync(
        long accountId, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var result = new List<ZohoAttachmentMeta>();
        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return result;

        using var form = new MultipartFormDataContent();
        var any = false;
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var bytes = await File.ReadAllBytesAsync(p, ct);
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "attach", Path.GetFileName(p));
            any = true;
        }
        if (!any) return result;

        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}/accounts/{accountId}/messages/attachments?uploadType=multipart&isInline=false")
        {
            Content = form
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray()) AddAttachmentMeta(result, item);
            }
            else AddAttachmentMeta(result, data);
        }
        catch { }
        return result;
    }

    private static void AddAttachmentMeta(List<ZohoAttachmentMeta> list, JsonElement item)
    {
        var store = item.TryGetProperty("storeName", out var s) ? s.GetString() : "";
        var name = item.TryGetProperty("attachmentName", out var n) ? n.GetString() : "";
        var path = item.TryGetProperty("attachmentPath", out var p) ? p.GetString() : "";
        if (!string.IsNullOrEmpty(store)) list.Add(new ZohoAttachmentMeta(store ?? "", name ?? "", path ?? ""));
    }

    private static string Truncate(string s, int max = 400)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] : s);

    /// <summary>把 列表摘要 + 内容 组装成 EmailMessage（内容获取失败返回 null）。</summary>
    public async Task<EmailMessage?> ToEmailMessageAsync(
        long accountId, long folderId, ZohoMessageSummary m, string folderName, CancellationToken ct = default)
    {
        var content = await GetMessageContentAsync(accountId, folderId, m.MessageId, ct);
        var body = "";
        var imageCids = new List<string>();
        if (content != null && content.Value.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            body = StripHtmlKeepImages(c.GetString() ?? "", imageCids);
        var maxChars = _config.MaxBodyChars;
        if (body.Length > maxChars) body = body[..maxChars];

        // 附件信息：普通附件记录元数据；内嵌图片（isInline）下载字节，按 cid 出现顺序对应
        var attachInfo = await GetAttachmentInfoAsync(accountId, folderId, m.MessageId, ct);
        var attachments = new List<EmailAttachment>();
        var inlineFiles = new List<string>();
        var inlineBytes = new List<byte[]>();
        if (attachInfo != null && attachInfo.Value.TryGetProperty("attachments", out var attsArr) && attsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attsArr.EnumerateArray())
            {
                var isInline = a.TryGetProperty("isInline", out var inl) && inl.ValueKind == JsonValueKind.True;
                var name = GetPropStr(a, "attachmentName");
                var id = GetPropStr(a, "attachmentId");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!isInline) { attachments.Add(new EmailAttachment(name, id)); continue; }
                var idx = inlineFiles.Count;
                var ext = Path.GetExtension(name);
                inlineFiles.Add($"img{idx}{(string.IsNullOrEmpty(ext) ? ".png" : ext)}");
                inlineBytes.Add(string.IsNullOrEmpty(id)
                    ? Array.Empty<byte>()
                    : await DownloadAttachmentAsync(accountId, folderId, m.MessageId, id, ct) ?? Array.Empty<byte>());
            }
        }

        return new EmailMessage
        {
            Folder = folderName,
            Uid = 0,
            MessageId = "",
            ZohoMessageId = m.MessageId.ToString(),
            ZohoThreadId = m.ThreadId,
            ZohoFolderId = folderId,
            FromAddress = CleanSingleAddress(m.FromAddress),
            FromName = "",
            ToAddresses = ExtractAddresses(m.ToAddress),
            CcAddresses = ExtractAddresses(m.CcAddress),
            Subject = m.Subject,
            DateSent = MsToSentDateTime(m.SentDate),
            DateReceived = MsToDateTime(m.ReceivedTime),
            BodyText = body,
            ContentHash = ComputeHash(body),
            Attachments = attachments,
            InlineImages = inlineFiles,
            InlineImageBytes = inlineBytes.Count > 0 ? inlineBytes : null
        };
    }

    /// <summary>取邮件的附件信息（attachmentinfo 端点，含 attachmentId/attachmentName/attachmentSize/isInline），返回 data 节点；失败返回 null。</summary>
    public async Task<JsonElement?> GetAttachmentInfoAsync(
        long accountId, long folderId, long messageId, CancellationToken ct = default)
        => await GetJsonAsync($"/accounts/{accountId}/folders/{folderId}/messages/{messageId}/attachmentinfo", ct);

    /// <summary>从 attachmentinfo 的 data 节点提取附件（名称+attachmentId；排除内嵌图片）。返回空列表表示无附件。</summary>
    public static List<EmailAttachment> ExtractAttachments(JsonElement? content)
    {
        var attachments = new List<EmailAttachment>();
        if (content == null) return attachments;
        if (!content.Value.TryGetProperty("attachments", out var atts) || atts.ValueKind != JsonValueKind.Array)
            return attachments;
        foreach (var a in atts.EnumerateArray())
        {
            if (a.TryGetProperty("isInline", out var inl) && inl.ValueKind == JsonValueKind.True) continue;
            var name = GetPropStr(a, "attachmentName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            attachments.Add(new EmailAttachment(name, GetPropStr(a, "attachmentId")));
        }
        return attachments;
    }

    /// <summary>下载某封邮件的某个附件（Zoho REST：GET .../messages/{messageId}/attachments/{attachmentId}），返回字节内容；失败返回 null。</summary>
    public async Task<byte[]?> DownloadAttachmentAsync(
        long accountId, long folderId, long messageId, string attachmentId, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/accounts/{accountId}/folders/{folderId}/messages/{messageId}/attachments/{attachmentId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
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

    /// <summary>从 to/cc 原始地址串（HTML 编码 “Name”&lt;email&gt;）提取所有 (邮箱, 姓名) 对；姓名可为空。</summary>
    public static IEnumerable<(string Email, string Name)> ExtractContactNames(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Enumerable.Empty<(string, string)>();
        var decoded = System.Net.WebUtility.HtmlDecode(s);
        var result = new List<(string, string)>();
        foreach (Match m in Regex.Matches(decoded, @"(?:\x22([^\x22]*)\x22\s*)?<([^<>]+)>"))
        {
            var name = m.Groups[1].Value.Trim();
            var email = m.Groups[2].Value.Trim();
            if (email.Length > 0) result.Add((email, name));
        }
        return result;
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

    /// <summary>HTML 转纯文本。</summary>
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

    /// <summary>HTML 转纯文本，但 <img cid> 保留为占位符（收集 cid 到列表），使正文内嵌图片能在对应位置显示。</summary>
    internal static string StripHtmlKeepImages(string html, List<string> imageCids)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = Regex.Replace(html,
            @"(?i)<img[^>]*\bsrc\s*=\s*[""']?(?<src>[^""'\s>]+)[""']?[^>]*>", m =>
            {
                var src = m.Groups["src"].Value;
                if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    imageCids.Add(src[4..]);
                    return TicketManager.Models.InlineImage.Placeholder(imageCids.Count - 1);
                }
                return "";
            });
        return StripHtml(html);
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

    internal static string GetPropStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
