using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

const string user = "lishi.02@zohomail.com";
const string monitored = "support@manageengine.com";
const string credFile = @"d:\MobileSync\邮件账号.txt";

// 从账号配置文件读取该账号的密码（不写死在代码里）
string pass = "";
foreach (var line in File.ReadAllLines(credFile))
{
    if (line.Trim().Equals(user, StringComparison.OrdinalIgnoreCase))
    {
        // 用户名所在行的下一行非空行即密码
        var found = false;
        foreach (var l2 in File.ReadAllLines(credFile))
        {
            if (!found && l2.Trim().Equals(user, StringComparison.OrdinalIgnoreCase)) { found = true; continue; }
            if (found && !string.IsNullOrWhiteSpace(l2)) { pass = l2.Trim(); break; }
        }
        break;
    }
}
if (string.IsNullOrEmpty(pass)) { Console.WriteLine("未在账号文件里找到密码"); return 1; }

using var client = new ImapClient();
client.ProxyClient = new Socks5Client("127.0.0.1", 10808);
try
{
    await client.ConnectAsync("imap.zoho.com", 993, SecureSocketOptions.SslOnConnect);
    await client.AuthenticateAsync(user, pass);
}
catch (Exception ex)
{
    Console.WriteLine($"连接/登录失败: {ex.Message}");
    return 1;
}

// 列出所有文件夹，找出发件箱名称
try
{
    foreach (var f in client.GetFolders(client.PersonalNamespaces[0]))
    {
        var isSent = (f.Attributes & FolderAttributes.Sent) != 0;
        Console.WriteLine($"  文件夹: {f.FullName}  {(isSent ? "<- 发件箱" : "")}");
    }
}
catch (Exception ex) { Console.WriteLine($"列出文件夹失败: {ex.Message}"); }

var folder = await client.GetFolderAsync("INBOX");
await folder.OpenAsync(FolderAccess.ReadOnly);

var uids = await folder.SearchAsync(SearchQuery.And(
    SearchQuery.NotDeleted,
    SearchQuery.SentSince(DateTime.Now.AddDays(-7))));
Console.WriteLine($"最近 7 天搜索到 UID 数: {uids.Count}");

bool Involves(InternetAddressList list) =>
    list.Mailboxes.Any(m => m.Address.Equals(monitored, StringComparison.OrdinalIgnoreCase));

int matched = 0, shown = 0;
foreach (var uid in uids)
{
    var m = await folder.GetMessageAsync(uid);
    var from = m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
    var isMon = Involves(m.From) || Involves(m.To) || Involves(m.Cc);
    if (isMon) matched++;
    if (shown < 20 || isMon)
    {
        var subj = m.Subject ?? "";
        if (subj.Length > 45) subj = subj[..45];
        var to = string.Join(",", m.To.Mailboxes.Select(x => x.Address));
        Console.WriteLine($"{(isMon ? "*命中* " : "      ")}{m.Date:MM-dd HH:mm} 发={from} 收={to} 主题={subj}");
        shown++;
    }
}
Console.WriteLine($"=== 涉及 {monitored} 的邮件数: {matched} / {uids.Count} ===");

await client.DisconnectAsync(true);
return 0;
