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

    // ===== SMTP 发送（未配置/不使用 Zoho REST 时的发信通道；“提新工单/回复”发信用） =====
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = ""; // 留空回退用 IMAP 账号
    public string SmtpPassword { get; set; } = ""; // 留空回退用 IMAP 密码

    // ===== 关注的客服邮箱 =====
    public List<string> MonitoredAddresses { get; set; } = new();

    // ===== 我方支持人员域名（同事邮箱的 @ 后缀；AI 方向标注时视为 [我]，避免误当客户） =====
    public List<string> MySupportDomains { get; set; } = new();

    // ===== 域名→企业 映射（用于从抄送/收件人地址推断企业） =====
    public Dictionary<string, string> DomainEnterpriseMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ===== DeepSeek / 主流 AI（OpenAI 兼容接口） =====
    public string DeepSeekApiKey { get; set; } = "";
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string AiProvider { get; set; } = "DeepSeek"; // 选中的 AI 提供商（设置页选择后自动填入接口地址与模型）
    public bool EnableAiTitle { get; set; } = true;
    public bool EnableAiStatus { get; set; } = true;
    public bool EnableAiMeta { get; set; } = true; // 主题未按约定标注产品/客户时，用 AI 分析补齐

    // ===== 代理 =====
    public bool UseProxy { get; set; }
    public string ProxyType { get; set; } = "Socks5"; // Socks4 | Socks5 | Http
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    public bool ProxyForImap { get; set; } = true;
    public bool ProxyForDeepSeek { get; set; }
    public bool ProxyForZoho { get; set; } // Zoho REST API 走代理（直连被封锁时必需，独立于总开关）
    public bool ProxyForSmtp { get; set; } // SMTP 发送走代理（独立于总开关）

    // ===== Zoho Mail REST API（IMAP 被封锁后的替代） =====
    public string ZohoApiBase { get; set; } = "https://mail.zoho.com/api";
    public string ZohoClientId { get; set; } = "";
    public string ZohoClientSecret { get; set; } = "";
    public string ZohoRefreshToken { get; set; } = "";
    public string ZohoAccountId { get; set; } = ""; // 留空自动获取

    // ===== 邮件字体（统一应用于 邮件正文 与 签名） =====
    public string EmailFontFamily { get; set; } = "Microsoft YaHei UI";
    public double EmailFontSize { get; set; } = 11;
    public string EmailFontColor { get; set; } = "#333333";

    // ===== 其他 =====
    public int FirstSyncDays { get; set; } = 365;
    public int MaxBodyChars { get; set; } = 6000;
    public int SyncConcurrency { get; set; } = 5; // 同步下载并发数（1~10，IMAP 与 Zoho REST 共用；过大可能被服务器限流/封禁）
    public bool EnableAutoSync { get; set; } = true; // 新邮件到达时自动收取（IMAP IDLE）
    public string SyncMode { get; set; } = "Auto"; // Auto|Zoho|Imap：用户选择的邮箱同步方式（Auto=有 Zoho 则 Zoho 否则 IMAP）
    public bool AutoTrackSupportMailboxes { get; set; } = true; // 扫描时自动发现并关注 @前含support、@后含manageengine/zohocorp 的客服邮箱
}
