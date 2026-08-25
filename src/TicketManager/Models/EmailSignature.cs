namespace TicketManager.Models;

/// <summary>邮件签名：名称 + 内容（发送时以 HTML 追加到邮件正文，字体/字号/颜色统一用全局邮件字体设置）。</summary>
public class EmailSignature
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}
