using System.Text.Json;
using TicketManager.Services;

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var wf = new WorkflowService(new DatabaseService(dbPath));
var cfg = wf.LoadConfig();
var api = new ZohoMailApiService(cfg);

var accountId = await api.GetAccountIdAsync();
Console.WriteLine($"accountId={accountId}");

// 自动回复：Id=2921 → ZohoMessageId=1787020047906158600，Inbox folderId=5796053000000008014
const string msgId = "1787020047906158600";
const long folderId = 5796053000000008014;
var path = $"/accounts/{accountId}/folders/{folderId}/messages/{msgId}/content?includeBlockContent=true";
var raw = await api.GetJsonRawAsync(path);
if (raw == null) { Console.WriteLine("获取失败"); return; }

Console.WriteLine($"=== 内容 API 原始响应 ===");
Console.WriteLine(raw.Length > 800 ? raw[..800] : raw);

// 检查列表 API 返回的字段
var listRaw = await api.GetJsonRawAsync($"/accounts/{accountId}/messages/view?folderId={folderId}&start=1&limit=2&sortBy=date&sortorder=false");
if (listRaw != null)
{
    Console.WriteLine($"\n=== 列表 API 首条消息字段 ===");
    using var doc = JsonDocument.Parse(listRaw);
    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        PrintKeys(data[0], 0, maxDepth: 2);
}

static void PrintKeys(JsonElement el, int depth, int maxDepth)
{
    if (el.ValueKind == JsonValueKind.Object)
    {
        foreach (var p in el.EnumerateObject())
        {
            string val = "";
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                var s = p.Value.GetString() ?? "";
                val = $" (\"{(s.Length > 50 ? s[..50] : s)}\")";
            }
            Console.WriteLine($"{new string(' ', depth * 2)}{p.Name}: {p.Value.ValueKind}{val}");
            if (depth < maxDepth && (p.Value.ValueKind == JsonValueKind.Object || p.Value.ValueKind == JsonValueKind.Array))
                PrintKeys(p.Value, depth + 1, maxDepth);
        }
    }
    else if (el.ValueKind == JsonValueKind.Array && depth < maxDepth)
    {
        foreach (var item in el.EnumerateArray()) PrintKeys(item, depth + 1, maxDepth);
    }
}
