using TicketManager.Services;
using TicketManager.ViewModels;

// 复制真实库到临时文件，避免改动正在使用的库
var realDb = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var tempDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
    $"filterprobe_{Guid.NewGuid():N}.db");
System.IO.File.Copy(realDb, tempDb, true);
Console.WriteLine($"临时库: {tempDb}");

var db = new DatabaseService(tempDb);
db.Initialize();
var wf = new WorkflowService(db);
var vm = new MainViewModel(wf);

// 1. Load + 开启“仅新邮件”过滤
vm.Load();
Console.WriteLine($"加载后线索数: {wf.LoadThreads().Count}");
vm.NewMailOnly = true;
Console.WriteLine($"开启“仅新邮件”后 NewMailOnly={vm.NewMailOnly}, 树显示客户组={vm.Customers.Count}, 线索={CountRoots(vm)}");

// 2. 模拟同步后的流程（SyncAsync 成功分支）：LoadThreads + MergeThreads
var threads = wf.LoadThreads();
vm.MergeThreads(threads);
Console.WriteLine($"模拟同步后 NewMailOnly={vm.NewMailOnly}, 树显示线索={CountRoots(vm)}");

// 3. 模拟 StarredOnly
vm.NewMailOnly = false;
vm.StarredOnly = true;
Console.WriteLine($"开启“仅星标”后 StarredOnly={vm.StarredOnly}, 树显示线索={CountRoots(vm)}");
threads = wf.LoadThreads();
vm.MergeThreads(threads);
Console.WriteLine($"模拟同步后 StarredOnly={vm.StarredOnly}, 树显示线索={CountRoots(vm)}");

// 4. 模拟检索关键字过滤（SearchText）
vm.StarredOnly = false;
var keyword = FindRareKeyword(wf.LoadThreads());
vm.SearchText = keyword;
Console.WriteLine($"开启检索 '{keyword}' 后 SearchText={vm.SearchText}, 树显示线索={CountRoots(vm)}（应 ≤ 全量）");
threads = wf.LoadThreads();
vm.MergeThreads(threads);
Console.WriteLine($"模拟同步后 SearchText={vm.SearchText}, 树显示线索={CountRoots(vm)}（若变多=未匹配线索被插入=过滤被破坏）");

db.Dispose();
System.IO.File.Delete(tempDb);

static int CountRoots(MainViewModel vm)
{
    int n = 0;
    foreach (var c in vm.Customers)
        foreach (var p in c.Products)
            n += p.Threads.Count;
    return n;
}

/// <summary>找一个只出现在少数线索中的关键字（主题/工单号），用于验证检索过滤在同步后是否被破坏。</summary>
static string FindRareKeyword(List<TicketManager.Models.TicketThread> threads)
{
    var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in threads)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tokens.Add(t.TicketNumber);
        foreach (var e in t.Emails)
        {
            foreach (var w in (e.Subject + " " + e.AiTitle).Split(
                new[] { ' ', '[', ']', '（', '）', '(', ')', '#', '*', '：', ':', '，', ',', '。', '.', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 2) tokens.Add(w);
        }
        foreach (var tok in tokens)
            freq[tok] = freq.TryGetValue(tok, out var c) ? c + 1 : 1;
    }
    // 取出现次数最少的 token（恰好能命中少量线索）
    var best = freq.OrderBy(kv => kv.Value).First();
    Console.WriteLine($"（选中关键字 '{best.Key}'，命中 {best.Value} 条线索）");
    return best.Key;
}
