using System.IO;
using System.Linq;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// SMTP 发送服务（“提新工单/回复”邮件在未配置 Zoho REST 时的回退通道）。
/// 支持代理（Socks4/Socks5/HTTP，受配置 ProxyForSmtp 控制）。
/// 账号/密码留空时回退用 IMAP 账号/密码；服务器（SmtpHost）为必填。
/// </summary>
public class SmtpSendService
{
    private readonly AppConfig _config;

    public SmtpSendService(AppConfig config) => _config = config;

    /// <summary>连接服务器并认证（供设置页“测试连接”）。返回 (成功, 信息)。</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            await client.ConnectAsync(_config.SmtpHost, _config.SmtpPort, SocketOptions(), ct);
            if (ResolveUsername().Length > 0)
                await client.AuthenticateAsync(ResolveUsername(), ResolvePassword(), ct);
            await client.DisconnectAsync(true, ct);
            return (true, $"✅ SMTP 连接成功：{_config.SmtpHost}:{_config.SmtpPort}（认证机制 {client.AuthenticationMechanisms.Count} 种）");
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

    /// <summary>发送一封 HTML 邮件（可带附件）。from 为发件邮箱，to/cc 为逗号或分号分隔的多地址。</summary>
    public async Task<(bool Success, string? Error)> SendAsync(
        string from, string to, string? cc, string subject, string htmlBody,
        IEnumerable<string>? attachmentPaths = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.SmtpHost))
            return (false, "未配置 SMTP 服务器（设置 → SMTP 发送）。");
        if (string.IsNullOrWhiteSpace(from))
            return (false, "未配置发件邮箱（SMTP 账号或同步设置中的 IMAP 账号）。");
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse(from));
            foreach (var a in SplitAddresses(to)) msg.To.Add(MailboxAddress.Parse(a));
            if (!string.IsNullOrWhiteSpace(cc))
                foreach (var a in SplitAddresses(cc)) msg.Cc.Add(MailboxAddress.Parse(a));
            msg.Subject = subject ?? "";

            var builder = new BodyBuilder { HtmlBody = htmlBody ?? "" };
            if (attachmentPaths != null)
                foreach (var p in attachmentPaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
                    builder.Attachments.Add(p);
            msg.Body = builder.ToMessageBody();

            using var client = CreateClient();
            await client.ConnectAsync(_config.SmtpHost, _config.SmtpPort, SocketOptions(), ct);
            if (ResolveUsername().Length > 0)
                await client.AuthenticateAsync(ResolveUsername(), ResolvePassword(), ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);
            return (true, null);
        }
        catch (AuthenticationException)
        {
            return (false, "SMTP 认证失败：账号或密码不正确（部分邮箱需用“授权码”而非登录密码）。");
        }
        catch (Exception ex)
        {
            return (false, $"SMTP 发送失败：{ex.Message}");
        }
    }

    private SmtpClient CreateClient()
    {
        var client = new SmtpClient();
        client.ProxyClient = ImapSyncService.CreateProxy(_config, _config.ProxyForSmtp);
        return client;
    }

    /// <summary>SSL 选项：SmtpUseSsl=true 走 SslOnConnect（465）；否则 Auto（优先 STARTTLS，587 等）。</summary>
    private SecureSocketOptions SocketOptions()
        => _config.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;

    private string ResolveUsername()
        => string.IsNullOrWhiteSpace(_config.SmtpUsername) ? (_config.ImapUsername ?? "") : _config.SmtpUsername;

    private string ResolvePassword()
        => string.IsNullOrWhiteSpace(_config.SmtpPassword) ? (_config.ImapPassword ?? "") : _config.SmtpPassword;

    /// <summary>地址串按逗号/分号拆分成纯邮箱（兼容 Zoho 逗号分隔与用户手输的分号分隔）。</summary>
    private static IEnumerable<string> SplitAddresses(string s) =>
        s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Where(a => a.Length > 0);
}
