using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using TicketManager.Models;

namespace TicketManager.Services;

public record SyncResult(List<EmailMessage> Emails, uint MaxUid);

/// <summary>IMAP 同步服务。支持代理（Socks4/Socks5/HTTP）。</summary>
public class ImapSyncService
{
    private readonly AppConfig _config;

    public ImapSyncService(AppConfig config) => _config = config;

    /// <summary>
    /// 同步指定文件夹。lastUid=0 表示首次同步：拉取最近 FirstSyncDays 天；
    /// 否则增量拉取 lastUid 之后的新邮件。
    /// 提供 onMonitoredEmail 回调时，命中“关注客服邮箱”的邮件会实时交给回调处理（便于逐封落库），
    /// 否则收集到返回列表中。onUidProcessed 每处理完一个 UID 即回调，便于实时推进同步游标。
    /// skipIfExists 提供“某 UID 是否已在本地”的判定时，已存在的邮件直接跳过、不再重复下载。
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        string folderName, uint lastUid,
        Func<EmailMessage, Task>? onMonitoredEmail = null,
        Action<uint>? onUidProcessed = null,
        Func<uint, bool>? skipIfExists = null,
        IProgress<string>? progress = null)
    {
        using var client = new ImapClient();
        client.ProxyClient = CreateProxy();

        var socketOptions = _config.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
        await client.ConnectAsync(_config.ImapHost, _config.ImapPort, socketOptions);
        await client.AuthenticateAsync(_config.ImapUsername, _config.ImapPassword);

        var folder = await client.GetFolderAsync(folderName);
        await folder.OpenAsync(FolderAccess.ReadOnly);

        SearchQuery query = lastUid > 0
            ? SearchQuery.Uids(new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue))
            : SearchQuery.And(
                SearchQuery.NotDeleted,
                SearchQuery.SentSince(DateTime.Now.AddDays(-_config.FirstSyncDays)));

        var uids = await folder.SearchAsync(query);

        var emails = new List<EmailMessage>();
        uint maxUid = lastUid;
        int i = 0, total = uids.Count;
        foreach (var uid in uids)
        {
            // 本地已存在该邮件（同文件夹+UID）→ 跳过，不再重复下载
            if (skipIfExists?.Invoke(uid.Id) != true)
            {
                var msg = await folder.GetMessageAsync(uid);
                var email = ToEmailMessage(uid, folderName, msg);
                if (email != null && IsMonitored(email))
                {
                    if (onMonitoredEmail != null) await onMonitoredEmail(email);
                    else emails.Add(email);
                }
            }
            if (uid.Id > maxUid) maxUid = uid.Id;
            onUidProcessed?.Invoke(uid.Id);
            i++;
            progress?.Report($"正在同步[{folderName}]… {i}/{total}");
        }

        await client.DisconnectAsync(true);
        return new SyncResult(emails, maxUid);
    }

    /// <summary>
    /// 用 IMAP IDLE 监听新邮件：连接并保持收件箱打开，收到“新邮件到达”通知时返回 true；
    /// 空闲超时（无新邮件）则继续监听。连接断开时抛出异常，由调用方重连。
    /// 服务器不支持 IDLE 时退化为定期 NOOP 轮询。
    /// </summary>
    public async Task<bool> WaitForNewMailAsync(string folderName, TimeSpan idleTimeout, CancellationToken ct)
    {
        using var client = new ImapClient();
        client.ProxyClient = CreateProxy();
        var socketOptions = _config.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
        await client.ConnectAsync(_config.ImapHost, _config.ImapPort, socketOptions, ct);
        await client.AuthenticateAsync(_config.ImapUsername, _config.ImapPassword, ct);

        var folder = await client.GetFolderAsync(folderName, ct) as ImapFolder
            ?? throw new InvalidOperationException("无法打开 IMAP 文件夹");
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        bool newMail = false;
        CancellationTokenSource? doneCts = null;
        // 有新邮件（或数量变化）时置标志并结束当前 IDLE
        void OnCountChanged(object? s, EventArgs e)
        {
            newMail = true;
            try { doneCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        folder.CountChanged += OnCountChanged;

        try
        {
            if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                while (!ct.IsCancellationRequested)
                {
                    newMail = false;
                    using var done = new CancellationTokenSource(idleTimeout);
                    doneCts = done;
                    try { await client.IdleAsync(done.Token, ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    if (newMail) return true; // 有新邮件到达
                }
            }
            else
            {
                // 服务器不支持 IDLE：退化为定期 NOOP 轮询
                while (!ct.IsCancellationRequested)
                {
                    newMail = false;
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    await client.NoOpAsync(ct);
                    if (newMail) return true;
                }
            }
            return false;
        }
        finally
        {
            folder.CountChanged -= OnCountChanged;
        }
    }

    /// <summary>
    /// 解析“已发送”文件夹：优先使用配置名；否则连接服务器按 Sent 属性递归扫描自动识别。
    /// 找不到返回 null。
    /// </summary>
    public async Task<string?> ResolveSentFolderAsync(string? configuredSent, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(configuredSent)) return configuredSent;

        using var client = new ImapClient();
        client.ProxyClient = CreateProxy();
        var socketOptions = _config.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
        await client.ConnectAsync(_config.ImapHost, _config.ImapPort, socketOptions, ct);
        await client.AuthenticateAsync(_config.ImapUsername, _config.ImapPassword, ct);

        var root = client.GetFolder(""); // 服务器根
        var sent = await FindSentRecursiveAsync(root, ct);
        return sent?.FullName;
    }

    private static async Task<IMailFolder?> FindSentRecursiveAsync(IMailFolder folder, CancellationToken ct)
    {
        foreach (var sub in await folder.GetSubfoldersAsync(false, ct))
        {
            if ((sub.Attributes & FolderAttributes.Sent) != 0) return sub;
            var nested = await FindSentRecursiveAsync(sub, ct);
            if (nested != null) return nested;
        }
        return null;
    }

    private IProxyClient? CreateProxy()
    {
        if (!_config.UseProxy || !_config.ProxyForImap) return null;
        if (string.IsNullOrEmpty(_config.ProxyHost)) return null;
        return _config.ProxyType switch
        {
            "Socks4" => new Socks4Client(_config.ProxyHost, _config.ProxyPort),
            "Socks5" => new Socks5Client(_config.ProxyHost, _config.ProxyPort),
            "Http" => new HttpProxyClient(_config.ProxyHost, _config.ProxyPort),
            _ => null
        };
    }

    /// <summary>任一关注客服邮箱出现在 收件人/抄送/发件人 中即纳入。</summary>
    private bool IsMonitored(EmailMessage e)
    {
        if (_config.MonitoredAddresses.Count == 0) return true;
        var participants = new List<string> { e.FromAddress };
        participants.AddRange(SplitList(e.ToAddresses));
        participants.AddRange(SplitList(e.CcAddresses));
        return _config.MonitoredAddresses.Any(m =>
            participants.Any(p => string.Equals(p, m, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> SplitList(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static EmailMessage ToEmailMessage(UniqueId uid, string folder, MimeMessage msg)
    {
        var body = GetBodyText(msg.Body) ?? "";
        return new EmailMessage
        {
            Folder = folder,
            Uid = uid.Id,
            MessageId = msg.MessageId ?? "",
            InReplyTo = msg.InReplyTo ?? "",
            References = string.Join(" ", msg.References ?? Enumerable.Empty<string>()),
            FromAddress = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            FromName = msg.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            ToAddresses = string.Join(";", msg.To.Mailboxes.Select(m => m.Address)),
            CcAddresses = string.Join(";", msg.Cc.Mailboxes.Select(m => m.Address)),
            Subject = msg.Subject ?? "",
            DateSent = msg.Date,
            DateReceived = msg.Date,
            BodyText = body,
            ContentHash = ComputeHash(body)
        };
    }

    /// <summary>递归提取纯文本正文（不含附件）。优先 text/plain，退化到 text/html 去标签。</summary>
    private static string GetBodyText(MimeEntity? entity)
    {
        if (entity == null) return "";
        switch (entity)
        {
            case TextPart text when text.ContentType.MediaSubtype.Equals("plain", StringComparison.OrdinalIgnoreCase):
                return text.Text ?? "";
            case TextPart html when html.ContentType.MediaSubtype.Equals("html", StringComparison.OrdinalIgnoreCase):
                return StripHtml(html.Text ?? "");
            case MultipartAlternative alt:
            {
                // 优先纯文本
                var plain = alt.OfType<TextPart>()
                    .FirstOrDefault(p => p.ContentType.MediaSubtype.Equals("plain", StringComparison.OrdinalIgnoreCase));
                if (plain != null) return plain.Text ?? "";
                var htmlPart = alt.OfType<TextPart>()
                    .FirstOrDefault(p => p.ContentType.MediaSubtype.Equals("html", StringComparison.OrdinalIgnoreCase));
                if (htmlPart != null) return StripHtml(htmlPart.Text ?? "");
                return string.Join("\n", alt.Select(GetBodyText));
            }
            case MultipartRelated related:
                return GetBodyText(related.Root);
            case Multipart mp:
            {
                var parts = mp.OfType<TextPart>().Select(p => p.Text ?? "").Where(t => t.Length > 0).ToList();
                return parts.Count > 0 ? string.Join("\n", parts) : string.Join("\n", mp.Select(GetBodyText));
            }
            default:
                return "";
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // 去掉 script/style
        html = System.Text.RegularExpressions.Regex.Replace(html, @"(?s)<(script|style)[^>]*>.*?</\1>", " ");
        // 块级标签换行
        html = System.Text.RegularExpressions.Regex.Replace(html, @"(?i)</?(p|div|br|li|tr|h[1-6])[^>]*>", "\n");
        // 其余标签去除
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"[ \t]+", " ");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    private static string ComputeHash(string text)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}
