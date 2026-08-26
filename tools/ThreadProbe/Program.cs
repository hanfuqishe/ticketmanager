using TicketManager.Models;
using TicketManager.Services;

// 复制真实库到临时文件，避免与正在运行的应用争用
var realDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var tempDb = Path.Combine(Path.GetTempPath(), $"threadprobe_{Guid.NewGuid():N}.db");
File.Copy(realDb, tempDb, true);
var db = new DatabaseService(tempDb);
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

// 专项：工单 13801919 线索成员（报障 13620 应并入成为根）
Console.WriteLine("\n=== 工单 13801919 线索 ===");
var target = threads.FirstOrDefault(t => t.TicketNumber == "13801919");
if (target == null)
{
    Console.WriteLine("!! 未找到 13801919 线程");
}
else
{
    Console.WriteLine($"工单={target.TicketNumber} 邮件数={target.Emails.Count}");
    foreach (var e in target.Emails.OrderBy(x => x.DateSent))
        Console.WriteLine($"  [{e.DateSent:MM-dd HH:mm}] {e.Folder,-5} 主题={e.Subject[..Math.Min(60, e.Subject.Length)]}");
    var hasReportRoot = target.Emails.Any(e =>
        e.Folder == "Sent" && e.Subject.Contains("Privileges are required when NCM backs up"));
    Console.WriteLine(hasReportRoot ? "✔ 报障邮件已并入该工单" : "✘ 报障邮件未并入该工单");
}

// 专项：OPM纳管存储设备需要技术支持 线索（应只含同会话的 3 封，12694/12679/自动回复不再混入）
Console.WriteLine("\n=== OPM纳管存储设备需要技术支持 线索 ===");
var opm = threads.FirstOrDefault(t => t.Emails.Any(e => e.Subject.Contains("OPM纳管存储设备需要技术支持")));
if (opm == null)
    Console.WriteLine("!! 未找到该线索");
else
{
    Console.WriteLine($"线索={opm.TicketNumber} 邮件数={opm.Emails.Count}");
    foreach (var e in opm.Emails.OrderBy(x => x.DateSent))
        Console.WriteLine($"  [{e.DateSent:MM-dd HH:mm}] {e.Folder,-5} {e.Subject[..Math.Min(55, e.Subject.Length)]}");
    bool hasForeign = opm.Emails.Any(e =>
        e.Subject.Contains("华为FC交换机") || e.Subject.Contains("测试OPManager") || e.Subject.Contains("Acknowledgement"));
    Console.WriteLine(hasForeign ? "✘ 仍有混入邮件" : "✔ 仅含同会话邮件");
}

// 专项：自动回复应并入各自工单（不再堆进大杂烩线索）
Console.WriteLine("\n=== 自动回复归属 ===");
foreach (var tid in new[] { "13411754", "13411805", "13415299", "13458877" })
{
    var t = threads.FirstOrDefault(x => x.TicketNumber == tid);
    var ack = t?.Emails.Count(e => e.Subject.Contains("Acknowledgement")) ?? 0;
    Console.WriteLine($"  工单 {tid}: 邮件数={t?.Emails.Count ?? 0}, 含自动回复={ack}");
}

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
try { File.Delete(tempDb); } catch { }

