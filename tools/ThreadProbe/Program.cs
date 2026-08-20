using TicketManager.Models;
using TicketManager.Services;

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(dbPath);
db.Initialize();
var all = db.LoadAllEmails();
Console.WriteLine($"共 {all.Count} 封邮件\n");

var threads = new ThreadBuilder().Build(all);

// 找到含自动回复("已收到您的工单")的线程，检查邮件数（报障并入效果）
Console.WriteLine("=== 含自动回复的线程 ===");
int linked = 0, orphan = 0;
foreach (var t in threads.OrderByDescending(t => t.Emails.Count))
{
    var hasAuto = t.Emails.Any(e => (e.Subject ?? "").Contains("已收到您的工单") || (e.BodyText ?? "").Contains("已经为您登记了工单"));
    if (!hasAuto) continue;
    var hasReport = t.Emails.Any(e => e.Folder == "Sent" && !e.Subject.Contains("回复"));
    var tag = hasReport ? "含报障" : "仅自动回复";
    if (hasReport) linked++; else orphan++;
    if (t.Emails.Count <= 3)
        Console.WriteLine($"  工单={t.TicketNumber} 邮件数={t.Emails.Count} {tag} 根={t.Emails.FirstOrDefault(e=>e.Parent==null)?.Subject[..Math.Min(30, (t.Emails.FirstOrDefault(e=>e.Parent==null)?.Subject ?? "").Length)]}");
}
Console.WriteLine($"\n含报障线程: {linked}, 仅自动回复(报障缺失): {orphan}");

