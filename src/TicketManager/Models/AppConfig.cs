namespace TicketManager.Models;

/// <summary>应用配置。敏感字段（密码、API Key）在持久化时使用 DPAPI 加密。</summary>
public class AppConfig
{
    // ===== IMAP =====
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public string ImapUsername { get; set; } = "";
    public string ImapPassword { get; set; } = "";
    public string ImapFolder { get; set; } = "INBOX";
    public string ImapSentFolder { get; set; } = ""; // 发件箱；留空则按 IMAP 属性自动识别

    // ===== 关注的客服邮箱 =====
    public List<string> MonitoredAddresses { get; set; } = new();

    // ===== DeepSeek =====
    public string DeepSeekApiKey { get; set; } = "";
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public bool EnableAiTitle { get; set; } = true;
    public bool EnableAiStatus { get; set; } = true;

    // ===== 代理 =====
    public bool UseProxy { get; set; }
    public string ProxyType { get; set; } = "Socks5"; // Socks4 | Socks5 | Http
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    public bool ProxyForImap { get; set; } = true;
    public bool ProxyForDeepSeek { get; set; }

    // ===== 其他 =====
    public int FirstSyncDays { get; set; } = 7;
    public int MaxBodyChars { get; set; } = 6000;
}
