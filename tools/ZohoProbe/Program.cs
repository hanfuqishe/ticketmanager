using System.IO;
using TicketManager.Services;

// 读取真实配置，用临时库验证 REST 同步的数据链路
var realDb = new DatabaseService();
var wf = new WorkflowService(realDb);
var cfg = wf.LoadConfig();
var api = new ZohoMailApiService(cfg);

var tmpPath = Path.Combine(Path.GetTempPath(), "zoho_probe_" + Guid.NewGuid().ToString("N") + ".db");
var db = new DatabaseService(tmpPath);
db.Initialize();

try
{
    var accountId = (await api.GetAccountIdAsync())!.Value;
    var folders = await api.GetFoldersAsync(accountId);
    var inbox = folders.First(f => f.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Inbox folderId={inbox.Id}");

    var list = await api.ListMessagesAsync(accountId, inbox.Id, 1, 3);
    Console.WriteLine($"列出 {list.Count} 封，开始逐封处理：\n");

    foreach (var m in list)
    {
        var exists = db.EmailExistsByZohoId("Inbox", m.MessageId.ToString());
        Console.WriteLine($"- msgId={m.MessageId} 已存在? {exists} | {m.Subject} | from={m.FromAddress}");
        if (exists) continue;

        var email = await api.ToEmailMessageAsync(accountId, inbox.Id, m, "Inbox");
        if (email == null) { Console.WriteLine("   (取内容失败)"); continue; }
        Console.WriteLine($"   → 组装: 主题={email.Subject} | 发件={email.FromAddress} | 收件={email.ToAddresses} | 抄送={email.CcAddresses}");
        Console.WriteLine($"   → 时间: sent={email.DateSent:yyyy-MM-dd HH:mm} recv={email.DateReceived:yyyy-MM-dd HH:mm} | 正文前60字: {email.BodyText[..Math.Min(60, email.BodyText.Length)]}");

        db.UpsertEmail(email);
        Console.WriteLine($"   → 已入库 Id={email.Id}, ZohoMessageId={email.ZohoMessageId}, 再次查询存在={db.EmailExistsByZohoId("Inbox", email.ZohoMessageId)}");
    }

    Console.WriteLine($"\n临时库共 {db.LoadAllEmails().Count} 封邮件");
}
catch (Exception ex)
{
    Console.WriteLine("EXC: " + ex);
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // 释放 SQLite 连接池对文件的占用
    try { System.IO.File.Delete(tmpPath); } catch { }
}




