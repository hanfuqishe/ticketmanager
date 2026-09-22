using System.Text.Json.Serialization;

namespace TicketManager.Models;

/// <summary>本地邮件草稿（提新工单 / 回复邮件 未发送前的内容快照），保存到 %AppData%\TicketManager\drafts.json。</summary>
public class EmailDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>草稿类型：submit=提新工单；reply=回复邮件。</summary>
    public string Kind { get; set; } = "";

    /// <summary>保存时间（yyyy-MM-dd HH:mm）。</summary>
    public string SavedAt { get; set; } = "";

    /// <summary>列表里显示的摘要（如 客户/故障现象 或 标题）。</summary>
    public string Title { get; set; } = "";

    // ---- 通用字段 ----
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string ExtraCc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Signature { get; set; } = "";
    public List<string> Attachments { get; set; } = new();

    // ---- 提新工单专用 ----
    public string Product { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Fault { get; set; } = "";

    /// <summary>类型显示名（列表展示用）。</summary>
    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        "submit" => "提新工单",
        "reply" => "回复邮件",
        _ => "草稿"
    };
}
