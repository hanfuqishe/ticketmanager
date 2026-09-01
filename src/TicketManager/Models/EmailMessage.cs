namespace TicketManager.Models;

/// <summary>邮件附件元数据（仅名称与下载标识，不保存内容）。Id 用于 Zoho 附件下载定位；IMAP 用 Folder+Uid+名称 定位。</summary>
public record EmailAttachment(string Name, string Id);

/// <summary>
/// 正文内嵌图片的占位符约定：同步时把 HTML 的 &lt;img&gt; 替换为 \u0001{序号}\u0002（控制字符，正常正文不会出现），
/// 图片内容存到 images/&lt;emailId&gt;/&lt;文件名&gt;；渲染时按序号加载对应文件显示。
/// </summary>
public static class InlineImage
{
    public const char MarkStart = '\u0001';
    public const char MarkEnd = '\u0002';

    /// <summary>生成第 index 张内嵌图片的占位符（插入正文文本流中）。</summary>
    public static string Placeholder(int index) => $"{MarkStart}{index}{MarkEnd}";

    /// <summary>匹配占位符的正则：\u0001(\d+)\u0002。</summary>
    public static readonly System.Text.RegularExpressions.Regex Pattern =
        new($@"{MarkStart}(\d+){MarkEnd}");

    /// <summary>正文是否含内嵌图片占位符。</summary>
    public static bool HasPlaceholder(string? text) => !string.IsNullOrEmpty(text) && text.Contains(MarkStart);
}

/// <summary>一封邮件。Parent/Children 仅在线程重建时使用，不持久化。</summary>
public class EmailMessage
{
    public long Id { get; set; }

    public string Folder { get; set; } = "INBOX";
    public uint Uid { get; set; }

    // ---- 邮件头 ----
    public string MessageId { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public string References { get; set; } = "";
    public string ZohoMessageId { get; set; } = ""; // Zoho REST 的 messageId（去重/游标用）
    public long? ZohoThreadId { get; set; } // Zoho REST 的线程 id（旧数据可为 null）

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ToAddresses { get; set; } = "";
    public string CcAddresses { get; set; } = "";

    public string Subject { get; set; } = "";
    public string AiTitle { get; set; } = "";

    public DateTimeOffset DateSent { get; set; }
    public DateTimeOffset DateReceived { get; set; }

    public string BodyText { get; set; } = "";
    public string ContentHash { get; set; } = "";

    // ---- 解析字段 ----
    public string TicketNumber { get; set; } = "";
    public string Product { get; set; } = "";
    public string Enterprise { get; set; } = "";
    public string FaultDescription { get; set; } = "";

    // ---- 同步标记 ----
    /// <summary>是否为最近同步新增的邮件（用于高亮与跳转）。</summary>
    public bool IsNew { get; set; }

    /// <summary>是否已标星（星标邮件，支持按星标过滤线索）。</summary>
    public bool Starred { get; set; }

    /// <summary>是否被用户忽略：被忽略的邮件不加入任何线索，单独收集到“被忽略的邮件”分组（树最底部）。</summary>
    public bool Ignored { get; set; }

    // ---- 附件（仅元数据，默认不下载内容；点击附件时按需下载并用系统默认应用打开）----
    public List<EmailAttachment> Attachments { get; set; } = new();

    /// <summary>Zoho REST 的文件夹 id（附件下载定位用；IMAP 邮件为 0）。</summary>
    public long ZohoFolderId { get; set; }

    /// <summary>正文内嵌图片的文件名列表（按正文中出现的顺序，对应占位符 <see cref="InlineImage.Placeholder"/> 的序号；未取到图片的项为空串）。</summary>
    public List<string> InlineImages { get; set; } = new();

    /// <summary>内嵌图片字节（瞬态，仅同步时用于落盘，不持久化）。与 <see cref="InlineImages"/> 并行。</summary>
    public List<byte[]>? InlineImageBytes { get; set; }

    // ---- 线程归属（由 ThreadBuilder 填充）----
    public long ThreadId { get; set; }

    // ---- 线程树（瞬态）----
    public EmailMessage? Parent { get; set; }
    public List<EmailMessage> Children { get; set; } = new();

    public string DisplaySender =>
        string.IsNullOrEmpty(FromName) ? FromAddress : $"{FromName} <{FromAddress}>";
}
