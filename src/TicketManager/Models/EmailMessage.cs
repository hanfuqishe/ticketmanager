namespace TicketManager.Models;

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

    // ---- 线程归属（由 ThreadBuilder 填充）----
    public long ThreadId { get; set; }

    // ---- 线程树（瞬态）----
    public EmailMessage? Parent { get; set; }
    public List<EmailMessage> Children { get; set; } = new();

    public string DisplaySender =>
        string.IsNullOrEmpty(FromName) ? FromAddress : $"{FromName} <{FromAddress}>";
}
