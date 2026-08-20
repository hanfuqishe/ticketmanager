using TicketManager.Services;

var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var wf = new WorkflowService(new DatabaseService(dbPath));
var cfg = wf.LoadConfig();
Console.WriteLine($"keyConfigured={!string.IsNullOrEmpty(cfg.DeepSeekApiKey)} model={cfg.DeepSeekModel}\n");

// 拿几个真实线程（优先有较多邮件的）
var threads = wf.LoadThreads().OrderByDescending(t => t.Emails.Count).Take(3).ToList();
foreach (var t in threads)
{
    Console.WriteLine($"=== 线程 工单={t.TicketNumber} 邮件数={t.Emails.Count} 产品={t.Product} 客户={t.Enterprise} ===");
    try
    {
        using var ai = new DeepSeekService(cfg);
        var r = await ai.SummarizeThreadAsync(t);
        Console.WriteLine(r == null
            ? "  -> 返回 NULL（总结失败）"
            : $"  -> OK: 状态={r.Value.Status} | 总结={r.Value.Summary}");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  -> EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
    }
    Console.WriteLine();
}
