using System.Text.Json;
using TicketManager.Services;

// 探针：确认 Zoho 邮件在“有附件”时 content 响应是否含 attachments 数组（附件名 + attachmentId）。
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(dbPath);
db.Initialize();
var wf = new WorkflowService(db);
wf.LoadConfig();
var api = new ZohoMailApiService(wf.Config);

// ---- 诊断：token 刷新请求的真实状态码与错误描述 ----
var cfg2 = wf.Config;
var diagUrl = "https://accounts.zoho.com/oauth/v2/token?" +
    $"refresh_token={Uri.EscapeDataString(cfg2.ZohoRefreshToken)}&grant_type=refresh_token&client_id={Uri.EscapeDataString(cfg2.ZohoClientId)}";
using (var hcDiag = new HttpClient())
{
    using var resp = await hcDiag.PostAsync(diagUrl, null);
    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"token 刷新 状态={(int)resp.StatusCode} {resp.StatusCode} 响应={body[..Math.Min(300, body.Length)]}");
}
var token = await api.GetAccessTokenAsync();
Console.WriteLine("api.GetAccessToken 结果: " + (token == null ? "失败" : "成功"));
if (token == null) return;

// accountId 失败自动重试（Zoho 偶发限流）
long acc = 0;
for (int i = 1; i <= 4 && acc == 0; i++)
{
    try { acc = await api.GetAccountIdAsync() ?? 0; } catch { }
    if (acc == 0 && i < 4) { Console.WriteLine($"accountId 获取失败，{10 * i} 秒后重试…"); await Task.Delay(10_000 * i); }
}
if (acc == 0) { Console.WriteLine("accountId 获取失败"); return; }
Console.WriteLine("accountId=" + acc);

// 用列表 API 找一封 hasAttachment=1 的邮件（最多翻 5 页）
using var hc = new HttpClient();
long? hasAttachZid = null;
long hasAttachFid = 0;
string hasAttachFolder = "";
var folders = await api.GetFoldersAsync(acc);
var inbox = folders.FirstOrDefault(f => f.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase));
if (inbox == null) { Console.WriteLine("未找到 Inbox"); return; }
for (int page = 1; page <= 5 && hasAttachZid == null; page++)
{
    var listReq = new HttpRequestMessage(HttpMethod.Get,
        $"{api.ApiBase}/accounts/{acc}/messages/view?folderId={inbox.Id}&start={page}&limit=100&sortBy=date&sortorder=false");
    listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Zoho-oauthtoken", token);
    using var listResp = await hc.SendAsync(listReq);
    if (!listResp.IsSuccessStatusCode) { Console.WriteLine($"列表页 {page} 失败 {listResp.StatusCode}"); break; }
    var listJson = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
    foreach (var m in listJson.RootElement.GetProperty("data").EnumerateArray())
    {
        var ha = m.TryGetProperty("hasAttachment", out var h) ? h.GetString() : "0";
        if (ha == "1")
        {
            hasAttachZid = long.Parse(m.GetProperty("messageId").GetString()!);
            hasAttachFid = long.Parse(m.GetProperty("folderId").GetString()!);
            hasAttachFolder = m.GetProperty("folderId").GetString()!;
            break;
        }
    }
}
Console.WriteLine("找到带附件邮件 zid=" + (hasAttachZid?.ToString() ?? "无"));
if (hasAttachZid == null) { Console.WriteLine("Inbox 前 500 封内无带附件邮件"); return; }

// 调 attachmentinfo 端点，验证附件字段结构
var info = await api.GetAttachmentInfoAsync(acc, hasAttachFid, hasAttachZid.Value);
if (info == null) { Console.WriteLine("获取 attachmentinfo 失败"); return; }
var raw = info.Value.GetRawText();
Console.WriteLine("attachmentinfo JSON(前4000)=\n" + raw[..Math.Min(4000, raw.Length)]);
var extracted = ZohoMailApiService.ExtractAttachments(info);
Console.WriteLine("ExtractAttachments 提取: " + (extracted.Count == 0 ? "（无附件）" : string.Join("; ", extracted.Select(x => x.Name + "#" + x.Id))));

// 验证下载端点
if (extracted.Count > 0)
{
    var a = extracted[0];
    var bytes = await api.DownloadAttachmentAsync(acc, hasAttachFid, hasAttachZid.Value, a.Id);
    Console.WriteLine($"下载附件 {a.Name}: " + (bytes == null ? "失败" : $"成功 {bytes.Length} 字节"));
}

// 针对用户报告的有附件邮件（Id=13618，8-26 08:31 Inbox，303674）验证回填与下载
long targetId = 13618;
var t = db.LoadAllEmails().FirstOrDefault(e => e.Id == targetId);
if (t == null) { Console.WriteLine($"DB 中未找到 Id={targetId}"); return; }
Console.WriteLine($"目标邮件 Id={t.Id} Folder={t.Folder} zid={t.ZohoMessageId} folderId={t.ZohoFolderId} 附件数={t.Attachments.Count} 主题={t.Subject}");
var ensured = wf.EnsureAttachments(t.Id);
Console.WriteLine("EnsureAttachments 回填: " + (ensured.Count == 0 ? "（无附件）" : string.Join("; ", ensured.Select(x => x.Name + "#" + x.Id))));
var reload = db.LoadAllEmails().First(e => e.Id == t.Id);
Console.WriteLine($"回填后 folderId={reload.ZohoFolderId} 附件数={reload.Attachments.Count}");
foreach (var a in reload.Attachments)
{
    var path = wf.DownloadAttachment(reload.Id, a.Name);
    Console.WriteLine($"  下载 {a.Name} → {(path ?? "（失败）")} 存在={path != null && File.Exists(path)}");
}
Console.WriteLine("完成");
