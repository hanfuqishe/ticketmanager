using System.IO;
using TicketManager.Models;
using TicketManager.Services;

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ✔ " : "  ✘ ") + what);
    if (!ok) failures++;
}

Console.WriteLine("=== 1. 主题格式解析 ===");
var p1 = SubjectParser.Parse("[###T2026-001###][防火墙][某某公司]无法连接外网");
Check(p1?.TicketNumber == "T2026-001", $"工单号解析 -> {p1?.TicketNumber}");
Check(p1?.Product == "防火墙", $"产品解析 -> {p1?.Product}");
Check(p1?.Enterprise == "某某公司", $"客户解析 -> {p1?.Enterprise}");
Check(p1?.Fault == "无法连接外网", $"故障现象解析 -> {p1?.Fault}");

var p2 = SubjectParser.Parse("回复: [###T2026-002###][数据库][乙公司]查询超时");
Check(p2?.TicketNumber == "T2026-002", $"带\"回复:\"前缀解析 -> {p2?.TicketNumber}");
Check(p2?.Product == "数据库", $"带前缀产品解析 -> {p2?.Product}");
Check(p2?.Fault == "查询超时", $"带前缀故障解析 -> {p2?.Fault}");

var p3 = SubjectParser.Parse("完全无关的主题");
Check(p3 == null, $"非标准主题返回 null（实际: {(p3 is null ? "null" : "非null")}）");

var p4 = SubjectParser.Parse("回复：回复：[## 308843 ##] [Endpoint Central][旺旺集团]MacOS上WPS被异常Block");
Check(p4?.TicketNumber == "308843", $"双井号工单号 -> {p4?.TicketNumber}");
Check(p4?.Product == "Endpoint Central", $"双井号产品 -> {p4?.Product}");
Check(p4?.Enterprise == "旺旺集团", $"双井号客户 -> {p4?.Enterprise}");
Check(p4?.Fault == "MacOS上WPS被异常Block", $"双井号故障 -> {p4?.Fault}");

var p5 = SubjectParser.Parse("[## 308034 ##] EC自定义报表3");
Check(p5?.TicketNumber == "308034" && p5?.Product == "" && p5?.Enterprise == "",
    $"仅工单号（无产品/客户） -> {p5?.TicketNumber}|{p5?.Product}|{p5?.Enterprise}");

var p6 = SubjectParser.Parse("[Endpoint Central][旺旺集团]MacOS上WPS被异常Block");
Check(p6?.TicketNumber == "" && p6?.Product == "Endpoint Central" && p6?.Enterprise == "旺旺集团",
    $"无工单号格式解析 -> 工单={p6?.TicketNumber}|产品={p6?.Product}|客户={p6?.Enterprise}");

var p7 = SubjectParser.Parse("【Endpoint Central】【旺旺集团】MacOS上WPS被异常Block");
Check(p7?.Product == "Endpoint Central" && p7?.Enterprise == "旺旺集团",
    $"【】括号解析 -> 产品={p7?.Product}|客户={p7?.Enterprise}");

var p8 = SubjectParser.Parse("[旺旺集团][Endpoint Central]MacOS上WPS被异常Block");
Check(p8?.Product == "Endpoint Central" && p8?.Enterprise == "旺旺集团",
    $"顺序颠倒+一英一中 -> 产品={p8?.Product}|客户={p8?.Enterprise}");

var p9 = SubjectParser.Parse("回复：[## 999001 ##] 【Endpoint Central】[旺旺集团]无法登录");
Check(p9?.TicketNumber == "999001" && p9?.Product == "Endpoint Central" && p9?.Enterprise == "旺旺集团",
    $"混合括号+工单号 -> {p9?.TicketNumber}|{p9?.Product}|{p9?.Enterprise}");

var p10 = SubjectParser.Parse("[EC][旺旺集团]MacOS上WPS被异常Block");
Check(p10?.Product == "Endpoint Central" && p10?.Enterprise == "旺旺集团",
    $"EC 简称规范化 -> 产品={p10?.Product}|客户={p10?.Enterprise}");

var p11 = SubjectParser.Parse("【OPM】【某某公司】监控告警");
Check(p11?.Product == "OPManager", $"OPM 简称规范化 -> 产品={p11?.Product}");

Console.WriteLine();
Console.WriteLine("=== 2. 线程重建与缩进折叠规则 ===");
Console.WriteLine("场景：首封 m1；m1 有 2 个回复 A(m2)、C(m4)；A 只有 1 个回复 B(m3)→应折叠为同级(深度1)；C 有 2 个回复 D(m5)、E(m6)→分支(深度2)");
Console.WriteLine("预期结构：m1(0) → m2(1), m3(1), m4(1) → m5(2), m6(2)");

var emails = new List<EmailMessage>();
EmailMessage Add(string id, DateTimeOffset dt, string inReplyTo = "", string refs = "")
{
    var e = new EmailMessage
    {
        MessageId = id, TicketNumber = "T1", Product = "P", Enterprise = "E",
        Subject = "[###T1###][P][E]测试", DateSent = dt, DateReceived = dt,
        InReplyTo = inReplyTo, References = refs, BodyText = "正文", ContentHash = "h"
    };
    emails.Add(e);
    return e;
}
var root = Add("m1", new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
Add("m2", new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "m1", "m1");
Add("m3", new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero), "m2", "m1 m2");
Add("m4", new DateTimeOffset(2026, 1, 1, 10, 30, 0, TimeSpan.Zero), "m1", "m1");
Add("m5", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), "m4", "m1 m4");
Add("m6", new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero), "m4", "m1 m4");

var threads = new ThreadBuilder().Build(emails);
Check(threads.Count == 1, $"应生成 1 条工单线程（实际 {threads.Count}）");
Check(threads[0].Emails.Count == 6, $"线程含 6 封邮件（实际 {threads[0].Emails.Count}）");

var flat = Flatten(threads[0].DisplayRoots).ToList();
foreach (var n in flat) Console.WriteLine($"    {n.Id}  深度={n.Depth}");
Check(flat.Count == 6, $"展示树共 6 个节点（实际 {flat.Count}）");
Check(flat[0] is ("m1", 0), "m1 深度 0（首封）");
Check(flat[1] is ("m2", 1), "m2 深度 1（回复）");
Check(flat[2] is ("m3", 1), "m3 深度 1（单链折叠，不再缩进）");
Check(flat[3] is ("m4", 1), "m4 深度 1（回复）");
Check(flat[4] is ("m5", 2), "m5 深度 2（多人回复→分支缩进）");
Check(flat[5] is ("m6", 2), "m6 深度 2（多人回复→分支缩进）");

Console.WriteLine();
Console.WriteLine("=== 4. 无工单号根邮件从回复继承工单号 ===");
var sEmails = new List<EmailMessage>();
EmailMessage SAdd(string id, string ticket, DateTimeOffset dt, string inReplyTo = "", string refs = "")
{
    var e = new EmailMessage
    {
        Id = sEmails.Count + 1, MessageId = id, TicketNumber = ticket,
        DateSent = dt, DateReceived = dt, InReplyTo = inReplyTo, References = refs
    };
    sEmails.Add(e);
    return e;
}
SAdd("s1", "", new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
SAdd("s2", "T9", new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), "s1", "s1");
SAdd("s3", "T9", new DateTimeOffset(2026, 8, 1, 11, 0, 0, TimeSpan.Zero), "s2", "s1 s2");
var sThreads = new ThreadBuilder().Build(sEmails);
Check(sThreads.Count == 1, $"应生成 1 条线程（实际 {sThreads.Count}）");
Check(sThreads[0].TicketNumber == "T9", $"线程工单号 T9（实际 {sThreads[0].TicketNumber}）");
Check(sThreads[0].Emails.Any(e => e.MessageId == "s1"), "发件箱根邮件 s1 被纳入该线程");
Check(sThreads[0].DisplayRoots.Count == 1 && sThreads[0].DisplayRoots[0].Email.MessageId == "s1",
    "线程根是发件箱的 s1（真正源头）");

Console.WriteLine();
Console.WriteLine("=== 5. 同工单内无关联邮件挂到最早根下（不并排成根） ===");
var oEmails = new List<EmailMessage>();
EmailMessage OAdd(string id, string ticket, DateTimeOffset dt, string inReplyTo = "", string refs = "")
{
    var e = new EmailMessage
    {
        Id = oEmails.Count + 1, MessageId = id, TicketNumber = ticket,
        DateSent = dt, DateReceived = dt, InReplyTo = inReplyTo, References = refs
    };
    oEmails.Add(e);
    return e;
}
OAdd("o1", "T5", new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero));
OAdd("o2", "T5", new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
OAdd("o3", "T5", new DateTimeOffset(2026, 8, 2, 11, 0, 0, TimeSpan.Zero));
var oThreads = new ThreadBuilder().Build(oEmails);
Check(oThreads.Count == 1, $"应生成 1 条线程（实际 {oThreads.Count}）");
var oFlat = Flatten(oThreads[0].DisplayRoots).ToList();
Check(oFlat.Count == 3, $"展示树 3 个节点（实际 {oFlat.Count}）");
Check(oFlat[0] is ("o1", 0), $"o1 深度 0（根） -> {oFlat[0]}");
Check(oFlat[1] is ("o2", 1), $"o2 深度 1（挂到根下） -> {oFlat[1]}");
Check(oFlat[2] is ("o3", 1), $"o3 深度 1（挂到根下） -> {oFlat[2]}");

Console.WriteLine();
Console.WriteLine("=== 3. SQLite 数据库读写 ===");
var dbPath = Path.Combine(Path.GetTempPath(), "tm_smoke_" + Guid.NewGuid().ToString("N") + ".db");
var db = new DatabaseService(dbPath);
db.Initialize();
var email = new EmailMessage
{
    Folder = "INBOX", Uid = 1, MessageId = "smoke-1", Subject = "s", TicketNumber = "T9",
    Product = "P", Enterprise = "E", DateSent = DateTimeOffset.Now, DateReceived = DateTimeOffset.Now,
    BodyText = "b", ContentHash = "c"
};
var id = db.UpsertEmail(email);
var loaded = db.LoadAllEmails();
Check(loaded.Any(e => e.Id == id && e.TicketNumber == "T9"), "邮件写入并读回");
db.UpdateAiTitle(id, "AI 标题");
Check(db.LoadAllEmails().First(e => e.Id == id).AiTitle == "AI 标题", "AI 标题更新");
db.SetSetting("k1", "v1");
Check(db.GetSetting("k1") == "v1", "Settings 读写");
db.ClearAiData();
Check(db.LoadAllEmails().First(e => e.Id == id).AiTitle == "", "ClearAiData 清空 AI 字段");
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
File.Delete(dbPath);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过 ✅" : $"存在 {failures} 个失败 ❌");
return failures == 0 ? 0 : 1;

static IEnumerable<(string Id, int Depth)> Flatten(IEnumerable<ThreadNode> roots)
{
    foreach (var r in roots)
    {
        yield return (r.Email.MessageId, r.Depth);
        foreach (var x in Flatten(r.Children)) yield return x;
    }
}
