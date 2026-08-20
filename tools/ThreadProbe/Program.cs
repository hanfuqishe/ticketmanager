using TicketManager.Models;
using TicketManager.Services;

// 读取真实数据库，用 ThreadBuilder 重建线程，检查报障邮件是否成为根。
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(dbPath);
db.Initialize();
var all = db.LoadAllEmails();
Console.WriteLine($"共 {all.Count} 封邮件\n");

// 模拟 WorkflowService 的线程构建（不含 AI）
var threads = new ThreadBuilder().Build(all);

Console.WriteLine($"共 {threads.Count} 条线程\n");
Console.WriteLine("=== 工单 295642（应包含报障邮件【旺旺】MDM CSR证书无法下载 作为根）===");
foreach (var t in threads.Where(t => t.TicketNumber == "295642"))
{
    Console.WriteLine($"线程: 工单={t.TicketNumber} 邮件数={t.Emails.Count}");
    Console.WriteLine($"  根: {t.Emails.FirstOrDefault(e => e.Parent == null)?.Subject}");
    Console.WriteLine($"  根文件夹: {t.Emails.FirstOrDefault(e => e.Parent == null)?.Folder}");
    Console.WriteLine("  前5封（按时间）:");
    foreach (var e in t.Emails.OrderBy(e => e.DateSent).Take(5))
        Console.WriteLine($"    [{e.Folder}] {e.Subject} | {e.TicketNumber}");
    Console.WriteLine();
}

Console.WriteLine("=== 各线程根节点的文件夹分布（应为：Sent 占多数）===");
var rootFolders = threads.Select(t =>
{
    var root = t.Emails.FirstOrDefault(e => e.Parent == null);
    return root?.Folder ?? "?";
}).GroupBy(x => x).Select(g => $"{g.Key}: {g.Count()}").ToList();
Console.WriteLine(string.Join(" | ", rootFolders));

Console.WriteLine("\n=== 未命名(无工单号)线程 样本 ===");
foreach (var t in threads.Where(t => string.IsNullOrEmpty(t.TicketNumber)).Take(8))
{
    var root = t.Emails.FirstOrDefault(e => e.Parent == null);
    Console.WriteLine($"  [根:{root?.Folder}] {root?.Subject} (邮件数 {t.Emails.Count})");
}
