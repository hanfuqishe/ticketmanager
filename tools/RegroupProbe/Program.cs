using System.Text;
using TicketManager.Services;

// 线索重归组：默认在**库副本**上演练（打印关键线程的成员变化），加 --apply 才重建真实库。
// 用法：dotnet run --project tools/RegroupProbe/RegroupProbe.csproj [--apply]
// 什么时候用：改了 ThreadBuilder 的归组规则后，让现有数据立即按新规则重新分组（否则要等下次“有新邮件”的同步）。
var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
var realDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var target = apply ? realDb : Path.Combine(Path.GetTempPath(), $"regroup_{Guid.NewGuid():N}.db");
if (!apply) File.Copy(realDb, target, true);

var db = new DatabaseService(target);
db.Initialize();
var sb = new StringBuilder();

sb.AppendLine($"库: {target}");
sb.AppendLine($"模式: {(apply ? "重建真实库（PersistThreads）" : "演练（库副本，不改原库）")}");

if (apply)
{
    new WorkflowService(db).RebuildThreads();
    sb.AppendLine("已重建线索表。");
}

var all = db.LoadAllEmails();
var threads = new ThreadBuilder().Build(all);
sb.AppendLine($"\n邮件 {all.Count} 封 → 线索 {threads.Count} 条\n");

// 1) 关注“工单号出现在主题里”的那些线索，看散件是否已并回
sb.AppendLine("=== 主题提到 8003692 / 8036572 / 6380320 / 7950409 的线索 ===");
foreach (var t in threads
             .Where(t => t.Emails.Any(e => e.Subject.Contains("8003692") || e.Subject.Contains("8036572")
                                        || e.Subject.Contains("6380320") || e.Subject.Contains("7950409")))
             .OrderBy(t => t.TicketNumber))
{
    sb.AppendLine($"  线索 {t.TicketNumber}（{t.Emails.Count} 封，{t.Emails.Min(e => e.DateSent):yyyy-MM-dd}~{t.Emails.Max(e => e.DateSent):yyyy-MM-dd}）");
    foreach (var e in t.Emails.OrderBy(e => e.DateSent))
        sb.AppendLine($"     {e.DateSent:yyyy-MM-dd HH:mm} {e.Folder,-5} {e.FromAddress,-32} {Cut(e.Subject, 72)}");
}

// 2) 仍然“无工单号且只有一封”的散件（提醒/满意度通知之类最容易漏）
sb.AppendLine("\n=== 仍然单封且无工单号的线索 ===");
foreach (var t in threads.Where(t => t.TicketNumber.Length == 0 && t.Emails.Count == 1).OrderBy(t => t.Emails[0].DateSent))
    sb.AppendLine($"  {t.Emails[0].DateSent:yyyy-MM-dd} {t.Emails[0].FromAddress,-32} {Cut(t.Emails[0].Subject, 72)}");

var report = Path.Combine(AppContext.BaseDirectory, "regroup.txt");
File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"报告: {report}");

static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";
