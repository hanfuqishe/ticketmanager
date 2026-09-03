using System.IO;
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

    /// <summary>测试 IMAP 连接：连接服务器→认证→打开收件箱。返回 (成功, 信息)。</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync()
    {
        try
        {
            using var client = new ImapClient();
            client.ProxyClient = CreateProxy();
            var socketOptions = _config.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
            await client.ConnectAsync(_config.ImapHost, _config.ImapPort, socketOptions);
            await client.AuthenticateAsync(_config.ImapUsername, _config.ImapPassword);
            var folder = await client.GetFolderAsync(string.IsNullOrEmpty(_config.ImapFolder) ? "INBOX" : _config.ImapFolder);
            await folder.OpenAsync(FolderAccess.ReadOnly);
            await client.DisconnectAsync(true);
            return (true, $"✅ 连接成功：已认证并打开收件箱 {folder.Name}（认证机制 {client.AuthenticationMechanisms.Count} 种）");
        }
        catch (AuthenticationException)
        {
            return (false, "❌ 认证失败：账号或密码不正确（部分邮箱需用“授权码”而非登录密码）。");
        }
        catch (Exception ex)
        {
            return (false, $"❌ 测试失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 同步指定文件夹。lastUid=0 表示首次同步：拉取最近 FirstSyncDays 天；否则增量拉取 lastUid 之后的新邮件。
    /// 两阶段：先单连接扫描 UID 列表，再 N 路并发下载（每条连接负责一段 UID，N=并发设置，与 Zoho REST 下载并发一致）；
    /// 命中“关注客服邮箱”的邮件交给 onMonitoredEmail 回调（落库用写锁串行化，避免 SQLite 竞争），
    /// 否则收集到返回列表中。skipIfExists 提供“某 UID 是否已在本地”的判定时，已存在的邮件直接跳过。
    /// 并发下游标无法逐 UID 单调推进，改为同步结束时一次性推进到本次最大 UID（onUidProcessed 在结尾以 maxUid 回调一次）。
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        string folderName, uint lastUid,
        Func<EmailMessage, Task>? onMonitoredEmail = null,
        Action<uint>? onUidProcessed = null,
        Func<uint, bool>? skipIfExists = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var socketOptions = _config.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
        SearchQuery query = lastUid > 0
            ? SearchQuery.Uids(new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue))
            : SearchQuery.And(SearchQuery.NotDeleted, SearchQuery.SentSince(DateTime.Now.AddDays(-_config.FirstSyncDays)));

        // ---- 阶段1：单连接扫描，拿到 UID 列表 ----
        UniqueId[] uids;
        using (var scout = new ImapClient())
        {
            scout.ProxyClient = CreateProxy();
            await scout.ConnectAsync(_config.ImapHost, _config.ImapPort, socketOptions, ct);
            await scout.AuthenticateAsync(_config.ImapUsername, _config.ImapPassword, ct);
            var folder = await scout.GetFolderAsync(folderName, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            uids = (await folder.SearchAsync(query, ct)).ToArray();
            await scout.DisconnectAsync(true, ct);
        }
        int total = uids.Length;
        if (total == 0) return new SyncResult(new List<EmailMessage>(), lastUid);

        // ---- 阶段2：N 路并发下载（每条连接负责一段 UID）；落库回调用写锁串行化 ----
        int concurrency = Math.Clamp(_config.SyncConcurrency, 1, 10);
        using var writeLock = new SemaphoreSlim(1, 1);
        var collected = new List<EmailMessage>();
        var maxUid = lastUid;
        var maxUidLock = new object();
        int done = 0;
        await Parallel.ForEachAsync(
            SplitIntoChunks(uids, concurrency),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (chunk, ct2) =>
            {
                using var client = new ImapClient();
                client.ProxyClient = CreateProxy();
                await client.ConnectAsync(_config.ImapHost, _config.ImapPort, socketOptions, ct2);
                await client.AuthenticateAsync(_config.ImapUsername, _config.ImapPassword, ct2);
                var folder = await client.GetFolderAsync(folderName, ct2);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct2);
                uint chunkMax = 0;
                foreach (var uid in chunk)
                {
                    ct2.ThrowIfCancellationRequested();
                    // 本地已存在该邮件（同文件夹+UID）→ 跳过，不再重复下载
                    if (skipIfExists?.Invoke(uid.Id) != true)
                    {
                        var msg = await folder.GetMessageAsync(uid, ct2);
                        var email = ToEmailMessage(uid, folderName, msg);
                        if (email != null && IsMonitored(email))
                        {
                            if (onMonitoredEmail != null)
                            {
                                await writeLock.WaitAsync(ct2);
                                try { await onMonitoredEmail(email); }
                                finally { writeLock.Release(); }
                            }
                            else lock (collected) collected.Add(email);
                        }
                    }
                    if (uid.Id > chunkMax) chunkMax = uid.Id;
                    var d = Interlocked.Increment(ref done);
                    progress?.Report($"正在同步[{folderName}]… {d}/{total}");
                }
                lock (maxUidLock) if (chunkMax > maxUid) maxUid = chunkMax;
            });

        // 游标在结束时一次性推进到本次最大 UID（并发下无法逐 UID 单调推进）
        onUidProcessed?.Invoke(maxUid);
        return new SyncResult(collected, maxUid);
    }

    /// <summary>把 UID 数组尽量均匀地分成 n 段（并发下载每段一条连接）。</summary>
    private static IEnumerable<UniqueId[]> SplitIntoChunks(UniqueId[] items, int n)
    {
        if (items.Length == 0) yield break;
        int size = Math.Max(1, (int)Math.Ceiling(items.Length / (double)n));
        for (int i = 0; i < items.Length; i += size)
            yield return items[i..Math.Min(i + size, items.Length)];
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

    /// <summary>按 文件夹+UID+附件名 下载附件内容（重连邮箱取该邮件，按文件名匹配附件部分）。失败返回 null。</summary>
    public byte[]? DownloadAttachment(string folderName, uint uid, string attachmentName)
    {
        var socketOptions = _config.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
        using var client = new ImapClient();
        client.ProxyClient = CreateProxy();
        client.Connect(_config.ImapHost, _config.ImapPort, socketOptions);
        client.Authenticate(_config.ImapUsername, _config.ImapPassword);
        var folder = client.GetFolder(folderName);
        folder.Open(FolderAccess.ReadOnly);
        var msg = folder.GetMessage(new UniqueId(uid));
        foreach (var att in msg.Attachments)
        {
            if (att is MimePart part && string.Equals(part.FileName, attachmentName, StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                return ms.ToArray();
            }
        }
        return null;
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

    private IProxyClient? CreateProxy() => CreateProxy(_config, _config.ProxyForImap);

    /// <summary>按配置创建 MailKit 代理客户端（供 IMAP/SMTP 等客户端共用）。启用开关按目标分别控制（ProxyForImap/ProxyForSmtp）。</summary>
    public static IProxyClient? CreateProxy(AppConfig config, bool enabled)
    {
        if (!config.UseProxy || !enabled) return null;
        if (string.IsNullOrEmpty(config.ProxyHost)) return null;
        return config.ProxyType switch
        {
            "Socks4" => new Socks4Client(config.ProxyHost, config.ProxyPort),
            "Socks5" => new Socks5Client(config.ProxyHost, config.ProxyPort),
            "Http" => new HttpProxyClient(config.ProxyHost, config.ProxyPort),
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
        // 正文：HTML 的 <img cid> 保留为占位符，收集 cid 用于提取内嵌图片
        var imageCids = new List<string>();
        var body = GetBodyTextKeepImages(msg.Body, imageCids) ?? "";
        var inlineFiles = new List<string>();
        var inlineBytes = new List<byte[]>();
        if (imageCids.Count > 0)
        {
            // cid → MimePart 映射（ContentId 可能带尖括号）
            var byCid = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
            foreach (var bp in msg.BodyParts)
                if (bp is MimePart mp && !string.IsNullOrEmpty(mp.ContentId))
                    byCid[mp.ContentId.Trim('<', '>')] = mp;
            for (int i = 0; i < imageCids.Count; i++)
            {
                inlineFiles.Add(""); // 默认无图，找到则填充文件名
                if (imageCids[i].Length == 0 || !byCid.TryGetValue(imageCids[i], out var part)) continue;
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                var ext = ExtensionFor(part.ContentType.MimeType);
                inlineFiles[i] = $"img{i}{ext}";
                inlineBytes.Add(ms.ToArray());
            }
        }
        // 附件：仅记录文件名（默认不下载内容；点击附件时按需下载）
        var attachments = new List<EmailAttachment>();
        foreach (var att in msg.Attachments)
            if (att is MimePart part && !string.IsNullOrEmpty(part.FileName))
                attachments.Add(new EmailAttachment(part.FileName, ""));
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
            ContentHash = ComputeHash(body),
            Attachments = attachments,
            InlineImages = inlineFiles,
            InlineImageBytes = inlineBytes.Count > 0 ? inlineBytes : null
        };
    }

    /// <summary>MIME 类型 → 文件扩展名（内嵌图片保存用）。</summary>
    private static string ExtensionFor(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            "image/tiff" => ".tif",
            _ => ".png"
        };
    }

    /// <summary>递归提取纯文本正文（不含附件）。优先 text/plain，退化到 text/html 去标签。</summary>
    private static string GetBodyText(MimeEntity? entity)
    {
        return GetBodyTextCore(entity, null);
    }

    /// <summary>递归提取正文，同时收集内嵌图片 cid（HTML 的 &lt;img cid&gt; 替换为占位符）。</summary>
    private static string GetBodyTextKeepImages(MimeEntity? entity, List<string> imageCids)
    {
        return GetBodyTextCore(entity, imageCids);
    }

    private static string GetBodyTextCore(MimeEntity? entity, List<string>? imageCids)
    {
        if (entity == null) return "";
        switch (entity)
        {
            case TextPart text when text.ContentType.MediaSubtype.Equals("plain", StringComparison.OrdinalIgnoreCase):
                return text.Text ?? "";
            case TextPart html when html.ContentType.MediaSubtype.Equals("html", StringComparison.OrdinalIgnoreCase):
                return imageCids != null ? StripHtmlKeepImages(html.Text ?? "", imageCids) : StripHtml(html.Text ?? "");
            case MultipartAlternative alt:
            {
                // 优先纯文本
                var plain = alt.OfType<TextPart>()
                    .FirstOrDefault(p => p.ContentType.MediaSubtype.Equals("plain", StringComparison.OrdinalIgnoreCase));
                if (plain != null) return plain.Text ?? "";
                var htmlPart = alt.OfType<TextPart>()
                    .FirstOrDefault(p => p.ContentType.MediaSubtype.Equals("html", StringComparison.OrdinalIgnoreCase));
                if (htmlPart != null) return imageCids != null ? StripHtmlKeepImages(htmlPart.Text ?? "", imageCids) : StripHtml(htmlPart.Text ?? "");
                return string.Join("\n", alt.Select(x => GetBodyTextCore(x, imageCids)));
            }
            case MultipartRelated related:
                return GetBodyTextCore(related.Root, imageCids);
            case Multipart mp:
            {
                var parts = mp.OfType<TextPart>().Select(p => p.Text ?? "").Where(t => t.Length > 0).ToList();
                return parts.Count > 0 ? string.Join("\n", parts) : string.Join("\n", mp.Select(x => GetBodyTextCore(x, imageCids)));
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

    /// <summary>HTML 转纯文本，但 <img cid> 保留为占位符（收集 cid 到列表），使正文内嵌图片能在对应位置显示。</summary>
    private static string StripHtmlKeepImages(string html, List<string> imageCids)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // <img src="cid:xxx"> → 占位符；非 cid 的外链图片直接删除标签（保持原行为）
        html = System.Text.RegularExpressions.Regex.Replace(html,
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
        html = StripHtml(html);
        return html;
    }

    private static string ComputeHash(string text)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}
