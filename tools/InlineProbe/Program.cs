using System.Reflection;
using MailKit;
using MimeKit;
using TicketManager.Models;
using TicketManager.Services;

// 探针：验证 IMAP 正文内嵌图片的提取逻辑（构造带 <img cid> 的邮件 → ToEmailMessage 提取占位符 + 图片字节）。
// 通过反射调用 private 的 ImapSyncService.ToEmailMessage。

// 1x1 透明 PNG
var pngBytes = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

// 构造带内嵌图片的 MimeMessage：正文 HTML 里 <img src="cid:pic1">
var builder = new BodyBuilder();
builder.HtmlBody = "<p>Hello world</p><img src=\"cid:pic1\"/><p>Tail text</p>";
var lp = (MimePart)builder.LinkedResources.Add("pic.png", new MemoryStream(pngBytes));
lp.ContentId = "pic1";
var msg = new MimeMessage();
msg.From.Add(new MailboxAddress("Sender", "s@example.com"));
msg.Subject = "inline test";
msg.Body = builder.ToMessageBody();

var method = typeof(ImapSyncService).GetMethod("ToEmailMessage",
    BindingFlags.NonPublic | BindingFlags.Static);
if (method == null) { Console.WriteLine("方法未找到"); return; }

var email = (EmailMessage?)method.Invoke(null, new object[] { new UniqueId(1), "INBOX", msg });
if (email == null) { Console.WriteLine("提取失败"); return; }

Console.WriteLine("BodyText: [" + email.BodyText.Replace("\u0001", "<").Replace("\u0002", ">") + "]");
Console.WriteLine("含占位符: " + InlineImage.HasPlaceholder(email.BodyText));
Console.WriteLine("InlineImages: " + (email.InlineImages.Count == 0 ? "（空）" : string.Join(",", email.InlineImages)));
Console.WriteLine("InlineImageBytes 数: " + (email.InlineImageBytes?.Count ?? 0));
if (email.InlineImageBytes is { Count: > 0 } && email.InlineImageBytes[0].SequenceEqual(pngBytes))
    Console.WriteLine("图片字节一致: True");

// 验证 StripHtmlKeepImages 对“无 cid 的外链图片”不保留占位符
var noCid = (string?)typeof(ImapSyncService).GetMethod("StripHtmlKeepImages",
    BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new object[] { "<img src=\"https://x/y.png\">文字", new List<string>() });
Console.WriteLine("外链图片(应无占位符): [" + (noCid ?? "").Replace("\u0001", "<").Replace("\u0002", ">") + "]");

Console.WriteLine();
Console.WriteLine("=== 历史邮件内嵌图片回填（真实 Zoho 邮件，Id=13656） ===");
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(dbPath);
db.Initialize();
var wf = new WorkflowService(db);
wf.LoadConfig();
var r = wf.EnsureInlineImages(13656);
Console.WriteLine($"Handled={r.Handled} NewBody含占位符={(r.NewBody != null && InlineImage.HasPlaceholder(r.NewBody))}");
var reload = db.LoadAllEmails().First(e => e.Id == 13656);
Console.WriteLine("DB BodyText 含占位符: " + InlineImage.HasPlaceholder(reload.BodyText));
Console.WriteLine("DB InlineImages: " + (reload.InlineImages.Count == 0 ? "（空）" : string.Join(",", reload.InlineImages)));
var imgDir = TicketManager.Services.InlineImageStorage.EmailDir(13656);
Console.WriteLine("图片目录内容: " + (Directory.Exists(imgDir) ? string.Join(",", Directory.GetFiles(imgDir).Select(Path.GetFileName)) : "（无）"));
