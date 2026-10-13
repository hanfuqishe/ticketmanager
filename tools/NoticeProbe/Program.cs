using System.Text;
using TicketManager.Models;
using TicketManager.Services;

// “工单受理通知”首封 → 真实标题 的验证：跑真实库，把结果写成 UTF-8 报告文件。
// 用法：dotnet run --project tools/NoticeProbe/NoticeProbe.csproj
Console.OutputEncoding = Encoding.UTF8;

var realDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var tempDb = Path.Combine(Path.GetTempPath(), $"noticeprobe_{Guid.NewGuid():N}.db");
File.Copy(realDb, tempDb, true);
var db = new DatabaseService(tempDb);
db.Initialize();
var all = db.LoadAllEmails();
var sb = new StringBuilder();
sb.AppendLine($"库: {realDb}（复制到临时文件后只读）");
sb.AppendLine($"邮件 {all.Count} 封");

// ---------- 1. 判据单测（正例取自真实库，负例是真实标题） ----------
sb.AppendLine("\n=== 判据单测 IsAutoNotice ===");
string[] notices =
{
    "[##307425##] 我们已收到您的工单，将很快为您处理",
    "回复：[##305932##] ，你的工单已关闭，请确认",
    "[##8324026##] Your ticket has been created",
    "答复：[## 314971 ##] 我们已收到您的工单，将很快为您处理",
};
string[] realTitles =
{
    "回复：[## 307425 ##] [旺旺集团][新功能]如何全选“所有办公室”",
    "[## 306963 ##] OPM关于某个接口\\专线，客户希望流量大于70%的时候告警",
    "如何全选“所有办公室”",
    "回复：[## 9089489 ##] [旺旺集团][EC]Windows终端在安装了代理之后1分钟自动重启",
    "工单创建失败，提示权限不足",
    "[## 314207 ##] 客户要求导出全部终端清单",
};
foreach (var s in notices) sb.AppendLine($"  {SubjectParser.IsAutoNotice(s),-5} 通知  {s}");
foreach (var s in realTitles) sb.AppendLine($"  {SubjectParser.IsAutoNotice(s),-5} 真标题 {s}");

// ---------- 2. 通知正文抠标题 ----------
sb.AppendLine("\n=== 通知正文抠标题 ExtractNotifiedTitle ===");
foreach (var e in all.Where(e => SubjectParser.IsAutoNotice(e.Subject)).Take(3))
{
    var quoted = SubjectParser.ExtractNotifiedTitle(e.BodyText);
    sb.AppendLine($"  [{e.Id}] {e.FromAddress}  主题={e.Subject}");
    sb.AppendLine($"        正文抠出={quoted}");
    sb.AppendLine($"        展示标题={SubjectParser.NotifiedTitleToDisplay(quoted)}");
}

// ---------- 3. 全库：逐线程算“真实标题” ----------
var threads = new ThreadBuilder().Build(all);
var byThread = all.Where(e => e.ThreadId > 0)
                  .GroupBy(e => e.ThreadId)
                  .ToDictionary(g => g.Key, g => g.OrderBy(e => e.DateSent).ToList());
int noticeFirst = 0, resolved = 0, unresolved = 0, otherFirst = 0;
sb.AppendLine("\n=== 首封是通知的线索 ===");
foreach (var t in threads)
{
    var emails = t.Emails.OrderBy(e => e.DateSent).ToList();
    if (emails.Count == 0) continue;
    var first = emails[0];
    if (!SubjectParser.IsAutoNotice(first.Subject)) { otherFirst++; continue; }
    noticeFirst++;
    var title = WorkflowService.ResolveNoticeTitle(emails, first);
    if (title.Length > 0) resolved++; else unresolved++;
    sb.AppendLine($"  {t.TicketNumber,9}  首封={first.Subject}");
    sb.AppendLine($"             标题={(title.Length > 0 ? title : "(线程里只有通知，暂无标题)")}");
}
sb.AppendLine($"\n线索 {threads.Count}：首封是通知 {noticeFirst}（定出标题 {resolved} / 定不出 {unresolved}），其它首封 {otherFirst}");

// ---------- 4. 误判风险：全库被判成“通知”的主题 ----------
var flagged = all.Where(e => SubjectParser.IsAutoNotice(e.Subject))
                 .Select(e => SubjectParser.Parse(e.Subject)?.Fault ?? e.Subject)
                 .GroupBy(s => s.Trim())
                 .OrderByDescending(g => g.Count());
sb.AppendLine("\n=== 全库被判成“通知”的主题（确认没有真标题被误判）===");
foreach (var g in flagged)
    sb.AppendLine($"  {g.Count(),4}  {g.Key}");

var report = Path.Combine(AppContext.BaseDirectory, "noticeprobe.txt");
File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"报告: {report}");
