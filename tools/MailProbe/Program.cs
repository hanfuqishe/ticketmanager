using System.Globalization;
using System.Text;
using TicketManager.Services;

// 查邮箱里某一天的邮件（走 Zoho REST），用来排查“为什么某封没入库”：
// 打印当天各文件夹里的邮件（发件人/收件人/抄送/主题/MessageId），并标出本地库是否已有。
// 用法：dotnet run --project tools/MailProbe/MailProbe.csproj -- 2026-05-08 [关键字]
var dayArg = args.Length > 0 ? args[0] : DateTime.Today.ToString("yyyy-MM-dd");
var keyword = args.Length > 1 ? args[1] : "";
// 第一个参数不是日期 → 全时段按主题关键字搜索
var searchAll = !DateTime.TryParseExact(dayArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day);
if (searchAll)
{
    keyword = dayArg;
    day = DateTime.Today;
    if (keyword.Length == 0) { Console.WriteLine("用法: MailProbe <yyyy-MM-dd> [关键字]  或  MailProbe <主题关键字>"); return 1; }
}

var realDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(realDb);
var config = new WorkflowService(db).LoadConfig();
if (string.IsNullOrEmpty(config.ZohoClientId) && string.IsNullOrEmpty(config.ZohoRefreshToken))
{
    Console.WriteLine("未配置 Zoho REST（Client ID / Refresh Token），无法查询邮箱。");
    return 1;
}

var api = new ZohoMailApiService(config);
var acc = await api.GetAccountIdAsync();
if (acc == null)
{
    Console.WriteLine($"取账号失败：{api.LastError}");
    return 1;
}

var folders = await api.GetFoldersAsync(acc.Value);
var sb = new StringBuilder();
sb.AppendLine($"账号 {acc}，文件夹 {folders.Count} 个：{string.Join("、", folders.Select(f => f.Name))}");
sb.AppendLine(searchAll ? $"全时段搜索主题含「{keyword}」的邮件" : $"查询日期 {day:yyyy-MM-dd}（本地时间），关键字 {(keyword.Length == 0 ? "(无)" : keyword)}");

// 只扫默认同步的三个夹子 + 其它夹子各扫前若干页（邮件列表按时间倒序）
foreach (var folder in folders)
{
    var hits = new List<(string Time, string From, string To, string Cc, string Subject, string MsgId, bool InDb)>();
    var start = 1;
    var stop = false;
    var scannedCount = 0;
    DateTimeOffset? oldest = null;
    while (!stop && start <= 20000)
    {
        var page = await api.ListMessagesAsync(acc.Value, folder.Id, start, 200);
        if (page.Count == 0) break;
        foreach (var m in page)
        {
            var recvMs = long.TryParse(m.ReceivedTime, out var r) ? r : 0;
            if (recvMs == 0) continue;
            var local = DateTimeOffset.FromUnixTimeMilliseconds(recvMs).ToLocalTime();
            scannedCount++;
            oldest = local;
            if (!searchAll && local.Date < day.Date.AddDays(-1)) { stop = true; break; }   // 已经翻过目标日期
            if (!searchAll && local.Date != day.Date) continue;
            var subject = m.Subject ?? "";
            if (keyword.Length > 0 && !subject.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            var inDb = db.EmailExistsByZohoId(folder.Name, m.MessageId.ToString());
            hits.Add((local.ToString(searchAll ? "yyyy-MM-dd HH:mm" : "HH:mm"), m.FromAddress ?? "", m.ToAddress ?? "", m.CcAddress ?? "", subject, m.MessageId.ToString(), inDb));
        }
        start += 200;
    }
    sb.AppendLine($"   [覆盖] {folder.Name}: 扫描 {scannedCount} 封，最早到 {(oldest?.ToString("yyyy-MM-dd HH:mm") ?? "-")}，命中 {hits.Count}");
    if (hits.Count == 0) continue;
    sb.AppendLine($"\n=== {folder.Name}（{hits.Count} 封）===");
    foreach (var h in hits.OrderBy(x => x.Time))
    {
        sb.AppendLine($"  {h.Time} {(h.InDb ? "[库里已有]" : "[库里没有]")}");
        sb.AppendLine($"     主题: {h.Subject}");
        sb.AppendLine($"     发件: {h.From}   收件: {h.To}   抄送: {h.Cc}");
        sb.AppendLine($"     MessageId: {h.MsgId}");
    }
}

var report = Path.Combine(AppContext.BaseDirectory, $"mailprobe_{day:yyyyMMdd}.txt");
File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"报告: {report}");
return 0;
