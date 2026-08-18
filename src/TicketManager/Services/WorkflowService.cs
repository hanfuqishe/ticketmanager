using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// 工作流编排：配置加载/保存、邮件同步、线程重建、AI 标题、AI 工单状态总结。
/// </summary>
public class WorkflowService
{
    private readonly DatabaseService _db;
    private AppConfig _config = new();

    public WorkflowService(DatabaseService db) => _db = db;

    public AppConfig Config => _config;

    // ================= 配置 =================

    public AppConfig LoadConfig()
    {
        _config = new AppConfig
        {
            ImapHost = _db.GetSetting("imap_host"),
            ImapPort = IntOr(_db.GetSetting("imap_port"), 993),
            ImapUseSsl = BoolOr(_db.GetSetting("imap_ssl"), true),
            ImapUsername = _db.GetSetting("imap_username"),
            ImapPassword = CredentialService.Unprotect(_db.GetSetting("imap_password")),
            ImapFolder = StrOr(_db.GetSetting("imap_folder"), "INBOX"),
            ImapSentFolder = _db.GetSetting("imap_sent_folder"),
            MonitoredAddresses = SplitList(_db.GetSetting("monitored_addresses")),

            DeepSeekApiKey = CredentialService.Unprotect(_db.GetSetting("deepseek_key")),
            DeepSeekBaseUrl = StrOr(_db.GetSetting("deepseek_baseurl"), "https://api.deepseek.com"),
            DeepSeekModel = StrOr(_db.GetSetting("deepseek_model"), "deepseek-chat"),
            EnableAiTitle = BoolOr(_db.GetSetting("ai_title"), true),
            EnableAiStatus = BoolOr(_db.GetSetting("ai_status"), true),

            UseProxy = BoolOr(_db.GetSetting("proxy_use"), false),
            ProxyType = StrOr(_db.GetSetting("proxy_type"), "Socks5"),
            ProxyHost = _db.GetSetting("proxy_host"),
            ProxyPort = IntOr(_db.GetSetting("proxy_port"), 1080),
            ProxyForImap = BoolOr(_db.GetSetting("proxy_imap"), true),
            ProxyForDeepSeek = BoolOr(_db.GetSetting("proxy_deepseek"), false),

            FirstSyncDays = IntOr(_db.GetSetting("first_sync_days"), 7),
            MaxBodyChars = IntOr(_db.GetSetting("max_body_chars"), 6000)
        };
        return _config;
    }

    public void SaveConfig(AppConfig c)
    {
        _db.SetSetting("imap_host", c.ImapHost);
        _db.SetSetting("imap_port", c.ImapPort.ToString());
        _db.SetSetting("imap_ssl", c.ImapUseSsl.ToString());
        _db.SetSetting("imap_username", c.ImapUsername);
        _db.SetSetting("imap_password", CredentialService.Protect(c.ImapPassword));
        _db.SetSetting("imap_folder", c.ImapFolder);
        _db.SetSetting("imap_sent_folder", c.ImapSentFolder);
        _db.SetSetting("monitored_addresses", string.Join(";", c.MonitoredAddresses));

        _db.SetSetting("deepseek_key", CredentialService.Protect(c.DeepSeekApiKey));
        _db.SetSetting("deepseek_baseurl", c.DeepSeekBaseUrl);
        _db.SetSetting("deepseek_model", c.DeepSeekModel);
        _db.SetSetting("ai_title", c.EnableAiTitle.ToString());
        _db.SetSetting("ai_status", c.EnableAiStatus.ToString());

        _db.SetSetting("proxy_use", c.UseProxy.ToString());
        _db.SetSetting("proxy_type", c.ProxyType);
        _db.SetSetting("proxy_host", c.ProxyHost);
        _db.SetSetting("proxy_port", c.ProxyPort.ToString());
        _db.SetSetting("proxy_imap", c.ProxyForImap.ToString());
        _db.SetSetting("proxy_deepseek", c.ProxyForDeepSeek.ToString());

        _db.SetSetting("first_sync_days", c.FirstSyncDays.ToString());
        _db.SetSetting("max_body_chars", c.MaxBodyChars.ToString());
        _config = c;
    }

    // ================= 同步 + 处理 =================

    /// <summary>执行一次完整流程：同步 → 落库 → 重建线程 → AI 标题 → AI 状态。返回新增邮件数。</summary>
    public async Task<int> SyncAndProcessAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        LoadConfig();

        // 1. 同步（收件箱 + 发件箱；逐封拉取即落库 + 实时推进游标：网络中断时已同步的邮件不丢，重试从断点继续）
        progress?.Report("正在连接邮箱…");
        var imap = new ImapSyncService(_config);
        var folders = new List<string> { _config.ImapFolder };
        var sentFolder = await imap.ResolveSentFolderAsync(_config.ImapSentFolder, ct);
        if (!string.IsNullOrWhiteSpace(sentFolder) &&
            !folders.Contains(sentFolder, StringComparer.OrdinalIgnoreCase))
            folders.Add(sentFolder);

        int newCount = 0;
        try
        {
            foreach (var folder in folders)
            {
                var lastUid = _db.GetLastUid(folder);
                var result = await imap.SyncAsync(folder, lastUid,
                    onMonitoredEmail: e =>
                    {
                        var parsed = SubjectParser.Parse(e.Subject);
                        if (parsed != null)
                        {
                            e.TicketNumber = parsed.TicketNumber;
                            e.Product = parsed.Product;
                            e.Enterprise = parsed.Enterprise;
                            e.FaultDescription = parsed.Fault;
                        }
                        e.Id = _db.UpsertEmail(e);
                        newCount++;
                        return Task.CompletedTask;
                    },
                    onUidProcessed: uid => _db.SetLastUid(folder, uid),
                    progress);
                _db.SetLastUid(folder, result.MaxUid);
            }
        }
        finally
        {
            // 即使中途失败，也把已入库的邮件重建为线程，保证界面能看到已同步的部分
            RebuildThreads();
        }

        // 4. AI 标题
        if (_config.EnableAiTitle)
            await GenerateTitlesAsync(progress, ct);

        // 5. AI 工单状态
        if (_config.EnableAiStatus)
            await GenerateStatusesAsync(progress, ct);

        return newCount;
    }

    public void RebuildThreads()
    {
        ReparseSubjects();
        var all = _db.LoadAllEmails();
        var threads = new ThreadBuilder().Build(all);
        _db.PersistThreads(threads);
        // 每个线程的首封邮件保留原主题，不做 AI 提炼
        _db.ClearFirstEmailTitles();
    }

    /// <summary>对库中所有邮件重新解析主题，补齐 工单号/产品/客户/故障 字段（幂等）。</summary>
    public void ReparseSubjects()
    {
        var all = _db.LoadAllEmails();
        foreach (var e in all)
        {
            var parsed = SubjectParser.Parse(e.Subject);
            if (parsed == null) continue;
            if (parsed.TicketNumber == e.TicketNumber && parsed.Product == e.Product &&
                parsed.Enterprise == e.Enterprise && parsed.Fault == e.FaultDescription) continue;
            _db.UpdateEmailMeta(e.Id, parsed);
        }
    }

    /// <summary>清空下载的本地邮件与工单（保留全部配置）。</summary>
    public void ClearAllData() => _db.ClearAllData();

    public List<TicketThread> LoadThreads()
    {
        // 每个线程的首封邮件始终保留原主题（对已保存的旧数据也立即生效）
        _db.ClearFirstEmailTitles();
        var threads = _db.LoadThreads();
        var emails = _db.LoadAllEmails();
        var displayByThread = new ThreadBuilder().BuildDisplayByThread(emails);
        foreach (var t in threads)
            if (displayByThread.TryGetValue(t.Id, out var roots))
                t.DisplayRoots = roots;
        return threads;
    }

    private async Task GenerateTitlesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.DeepSeekApiKey)) return;
        var pending = _db.GetEmailsPendingTitle();
        if (pending.Count == 0) return;
        // 跳过每个线程的首封邮件（保持原主题）
        var firstIds = _db.GetFirstEmailIdsPerThread();
        pending = pending.Where(e => !firstIds.Contains(e.Id)).ToList();
        if (pending.Count == 0) return;
        using var ai = new DeepSeekService(_config);
        int done = 0;
        var tasks = pending.Select(async e =>
        {
            ct.ThrowIfCancellationRequested();
            var title = await ai.SummarizeTitleAsync(e);
            if (!string.IsNullOrWhiteSpace(title))
                _db.UpdateAiTitle(e.Id, title.Trim().Trim('"', '「', '」'));
            var d = Interlocked.Increment(ref done);
            progress?.Report($"正在生成标题… {d}/{pending.Count}");
        });
        await Task.WhenAll(tasks);
    }

    private async Task GenerateStatusesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.DeepSeekApiKey)) return;
        var threads = _db.LoadThreads();
        if (threads.Count == 0) return;
        using var ai = new DeepSeekService(_config);
        int done = 0;
        var tasks = threads.Select(async t =>
        {
            ct.ThrowIfCancellationRequested();
            var r = await ai.SummarizeThreadAsync(t);
            if (r != null)
                _db.UpdateThreadStatus(t.Id, r.Value.Status, r.Value.Summary);
            var d = Interlocked.Increment(ref done);
            progress?.Report($"正在总结工单状态… {d}/{threads.Count}");
        });
        await Task.WhenAll(tasks);
    }

    // ================= helpers =================

    private static int IntOr(string s, int def) => int.TryParse(s, out var v) ? v : def;
    private static bool BoolOr(string s, bool def) => bool.TryParse(s, out var v) ? v : def;
    private static string StrOr(string s, string def) => string.IsNullOrEmpty(s) ? def : s;

    private static List<string> SplitList(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
