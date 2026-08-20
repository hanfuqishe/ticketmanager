using System.Text.Json;
using MailKit;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// 工作流编排：配置加载/保存、邮件同步、线程重建、AI 标题、AI 工单状态总结。
/// </summary>
public class WorkflowService
{
    private readonly DatabaseService _db;
    private AppConfig _config = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1); // 串行化同步，避免手动与自动同步并发

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
            DomainEnterpriseMappings = ParseMappings(_db.GetSetting("domain_mappings")),

            DeepSeekApiKey = CredentialService.Unprotect(_db.GetSetting("deepseek_key")),
            DeepSeekBaseUrl = StrOr(_db.GetSetting("deepseek_baseurl"), "https://api.deepseek.com"),
            DeepSeekModel = StrOr(_db.GetSetting("deepseek_model"), "deepseek-chat"),
            EnableAiTitle = BoolOr(_db.GetSetting("ai_title"), true),
            EnableAiStatus = BoolOr(_db.GetSetting("ai_status"), true),
            EnableAiMeta = BoolOr(_db.GetSetting("ai_meta"), true),

            UseProxy = BoolOr(_db.GetSetting("proxy_use"), false),
            ProxyType = StrOr(_db.GetSetting("proxy_type"), "Socks5"),
            ProxyHost = _db.GetSetting("proxy_host"),
            ProxyPort = IntOr(_db.GetSetting("proxy_port"), 1080),
            ProxyForImap = BoolOr(_db.GetSetting("proxy_imap"), true),
            ProxyForDeepSeek = BoolOr(_db.GetSetting("proxy_deepseek"), false),

            FirstSyncDays = IntOr(_db.GetSetting("first_sync_days"), 7),
            MaxBodyChars = IntOr(_db.GetSetting("max_body_chars"), 6000),
            EnableAutoSync = BoolOr(_db.GetSetting("auto_sync"), true)
        };
        return _config;
    }

    public void SaveConfig(AppConfig c)
    {
        // 记录旧的关注邮箱，用于判断是否变化（变化则重置同步游标，重拉时间窗口内符合新条件的邮件）
        var oldMonitored = _config.MonitoredAddresses;
        _db.SetSetting("imap_host", c.ImapHost);
        _db.SetSetting("imap_port", c.ImapPort.ToString());
        _db.SetSetting("imap_ssl", c.ImapUseSsl.ToString());
        _db.SetSetting("imap_username", c.ImapUsername);
        _db.SetSetting("imap_password", CredentialService.Protect(c.ImapPassword));
        _db.SetSetting("imap_folder", c.ImapFolder);
        _db.SetSetting("imap_sent_folder", c.ImapSentFolder);
        _db.SetSetting("monitored_addresses", string.Join(";", c.MonitoredAddresses));
        _db.SetSetting("domain_mappings", JsonSerializer.Serialize(c.DomainEnterpriseMappings));

        _db.SetSetting("deepseek_key", CredentialService.Protect(c.DeepSeekApiKey));
        _db.SetSetting("deepseek_baseurl", c.DeepSeekBaseUrl);
        _db.SetSetting("deepseek_model", c.DeepSeekModel);
        _db.SetSetting("ai_title", c.EnableAiTitle.ToString());
        _db.SetSetting("ai_status", c.EnableAiStatus.ToString());
        _db.SetSetting("ai_meta", c.EnableAiMeta.ToString());

        _db.SetSetting("proxy_use", c.UseProxy.ToString());
        _db.SetSetting("proxy_type", c.ProxyType);
        _db.SetSetting("proxy_host", c.ProxyHost);
        _db.SetSetting("proxy_port", c.ProxyPort.ToString());
        _db.SetSetting("proxy_imap", c.ProxyForImap.ToString());
        _db.SetSetting("proxy_deepseek", c.ProxyForDeepSeek.ToString());

        _db.SetSetting("first_sync_days", c.FirstSyncDays.ToString());
        _db.SetSetting("max_body_chars", c.MaxBodyChars.ToString());
        _db.SetSetting("auto_sync", c.EnableAutoSync.ToString());
        _config = c;
        // 关注的客服邮箱发生变化（新增/删除）→ 重置同步游标，下次同步重新拉取时间窗口内的邮件
        if (!SameMonitoredSet(oldMonitored, c.MonitoredAddresses))
            ResetSyncCursors();
    }

    private static bool SameMonitoredSet(List<string> a, List<string> b)
    {
        var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return sa.SetEquals(sb);
    }

    private void ResetSyncCursors() => _db.ResetSyncCursors();

    // ================= 同步 + 处理 =================

    /// <summary>执行一次完整流程：同步 → 落库 → 重建线程 → AI 标题 → AI 状态。返回新增邮件数。串行化避免并发同步。</summary>
    public async Task<int> SyncAndProcessAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            return await SyncAndProcessCoreAsync(progress, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<int> SyncAndProcessCoreAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        LoadConfig();

        // 1. 同步（收件箱 + 发件箱；逐封拉取即落库 + 实时推进游标：网络中断时已同步的邮件不丢，重试从断点继续）
        progress?.Report("正在连接邮箱…");
        var imap = new ImapSyncService(_config);
        int newCount = 0;
        const int maxAttempts = 4; // 首次 + 最多 3 次自动重连重试
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    newCount = await SyncFoldersAsync(imap, progress, ct);
                    break; // 同步成功
                }
                catch (Exception ex) when (IsRetryableNetworkError(ex) && attempt < maxAttempts)
                {
                    progress?.Report($"网络中断（{ex.Message}），正在自动重连重试（第 {attempt}/{maxAttempts - 1} 次）…");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); // 逐次延长等待
                }
            }
        }
        finally
        {
            // 即使中途失败，也把已入库的邮件重建为线程，保证界面能看到已同步的部分
            RebuildThreads();
        }

        // 3. 域名→企业 自动学习 + 映射补齐 + AI 分析（均只填空缺），再重建线程分组
        progress?.Report("正在学习域名→企业映射…");
        AutoLearnDomainMappings();
        ApplyDomainMappings();
        if (_config.EnableAiMeta)
            await EnrichMissingMetaAsync(progress, ct);
        RebuildThreads();

        // 4. AI 标题
        if (_config.EnableAiTitle)
            await GenerateTitlesAsync(progress, ct);

        // 5. AI 工单状态
        if (_config.EnableAiStatus)
            await GenerateStatusesAsync(progress, ct);

        return newCount;
    }

    /// <summary>同步 收件箱 + 已发送 文件夹（含发件箱自动识别），逐封落库、逐 UID 推进游标，返回新增邮件数。</summary>
    private async Task<int> SyncFoldersAsync(ImapSyncService imap, IProgress<string>? progress, CancellationToken ct)
    {
        var folders = new List<string> { _config.ImapFolder };
        var sentFolder = await imap.ResolveSentFolderAsync(_config.ImapSentFolder, ct);
        if (!string.IsNullOrWhiteSpace(sentFolder) &&
            !folders.Contains(sentFolder, StringComparer.OrdinalIgnoreCase))
            folders.Add(sentFolder);

        int newCount = 0;
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
                skipIfExists: uid => _db.EmailExistsByUid(folder, uid),
                progress);
            _db.SetLastUid(folder, result.MaxUid);
        }
        return newCount;
    }

    /// <summary>判断是否为可自动重试的网络类错误（连接中断/超时/协议断开）。认证失败、用户取消除外。</summary>
    private static bool IsRetryableNetworkError(Exception ex)
    {
        if (ex is OperationCanceledException or MailKit.Security.AuthenticationException) return false;
        return ex is System.IO.IOException
            or System.Net.Sockets.SocketException
            or System.TimeoutException
            or MailKit.ProtocolException;
    }

    /// <summary>
    /// 自动收取循环：用 IMAP IDLE 持续监听收件箱，收到“新邮件到达”通知即自动执行一次完整同步；
    /// 连接中断自动等待后重连。由主视图在 启用自动收取 且 已配置邮箱 时启动，取消令牌停止。
    /// </summary>
    public async Task RunAutoSyncLoopAsync(Action<int>? onSynced, IProgress<string>? progress, CancellationToken ct)
    {
        var imap = new ImapSyncService(_config);
        while (!ct.IsCancellationRequested)
        {
            bool newMail;
            try
            {
                progress?.Report("正在监听新邮件…");
                newMail = await imap.WaitForNewMailAsync(_config.ImapFolder, TimeSpan.FromMinutes(25), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                progress?.Report($"自动收取连接中断（{ex.Message}），稍后自动重连…");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            if (ct.IsCancellationRequested || !newMail) continue;
            try
            {
                progress?.Report("检测到新邮件，正在自动同步…");
                var n = await SyncAndProcessAsync(progress, ct);
                onSynced?.Invoke(n);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                progress?.Report($"自动同步失败：{ex.Message}");
            }
        }
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
            // 只填空缺：已存在的 产品/客户（手工指定或 AI 分析）不被主题解析覆盖
            var product = e.Product.Length > 0 ? e.Product : parsed.Product;
            var enterprise = e.Enterprise.Length > 0 ? e.Enterprise : parsed.Enterprise;
            enterprise = ResolveEnterpriseName(enterprise); // 英文企业名对照 域名→企业 映射表翻译成中文
            if (parsed.TicketNumber == e.TicketNumber && product == e.Product &&
                enterprise == e.Enterprise && parsed.Fault == e.FaultDescription) continue;
            _db.UpdateEmailMeta(e.Id, new ParsedSubject(parsed.TicketNumber, product, enterprise, parsed.Fault));
        }
    }

    /// <summary>
    /// 英文企业名对照 域名→企业 映射表翻译成中文：若企业名与某映射域名的某个标签匹配
    /// （如 want-want ↔ want-want.com），则用该映射的中文企业名替换；已是中文或找不到匹配则原样返回。
    /// </summary>
    public string ResolveEnterpriseName(string enterprise)
    {
        if (string.IsNullOrEmpty(enterprise) || SubjectParser.ContainsCjk(enterprise)) return enterprise;
        foreach (var kv in _config.DomainEnterpriseMappings)
            if (DomainContainsLabel(kv.Key, enterprise))
                return kv.Value;
        return enterprise;
    }

    /// <summary>域名是否包含与某名称相同的标签（不区分大小写，如 want-want.com 含 want-want）。</summary>
    private static bool DomainContainsLabel(string domain, string name)
    {
        foreach (var label in domain.Split('.'))
            if (string.Equals(label.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>对缺客户的邮件，从其 发件/收件/抄送 地址的邮箱域名按 域名→企业 映射补齐企业。</summary>
    public void ApplyDomainMappings()
    {
        var mappings = _config.DomainEnterpriseMappings;
        if (mappings.Count == 0) return;
        var all = _db.LoadAllEmails();
        foreach (var e in all)
        {
            if (!string.IsNullOrEmpty(e.Enterprise)) continue;
            var enterprise = FindEnterpriseFromAddresses(e, mappings);
            if (string.IsNullOrEmpty(enterprise)) continue;
            _db.UpdateEmailMeta(e.Id, new ParsedSubject(e.TicketNumber, e.Product, enterprise, e.FaultDescription));
        }
    }

    /// <summary>从 发件/收件/抄送 地址域名（含子域后缀）匹配映射，返回企业名称。</summary>
    private static string FindEnterpriseFromAddresses(EmailMessage e, Dictionary<string, string> mappings)
    {
        var addresses = new List<string> { e.FromAddress };
        addresses.AddRange(SplitList(e.ToAddresses));
        addresses.AddRange(SplitList(e.CcAddresses));
        foreach (var addr in addresses)
        {
            var at = addr.LastIndexOf('@');
            if (at < 0 || at == addr.Length - 1) continue;
            var domain = addr[(at + 1)..].Trim();
            if (domain.Length == 0) continue;
            if (IsVendorDomain(domain)) continue;                              // 厂家域名（zoho/manageengine）不是客户
            foreach (var kv in mappings)
            {
                if (string.Equals(domain, kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }
        return "";
    }

    /// <summary>厂家域名：含 zoho 或 manageengine 的是本产品厂家（Zoho/ManageEngine）人员，不是客户企业。</summary>
    private static bool IsVendorDomain(string domain) =>
        domain.Contains("zoho", StringComparison.OrdinalIgnoreCase) ||
        domain.Contains("manageengine", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 自动学习 域名→企业 映射：对主题中已用方括弧标注企业名称的邮件（这类邮件往往在首封中抄送了客户），
    /// 从其 发件/收件/抄送 地址的域名推断 域名→企业 并自动加入映射表。
    /// 不覆盖已有映射、跳过 本机账号/客服邮箱 的域名（那些不可能是客户企业域名）。
    /// </summary>
    public void AutoLearnDomainMappings()
    {
        var mappings = _config.DomainEnterpriseMappings;
        var serviceDomains = GetServiceDomains();
        var toAdd = new List<(string Domain, string Enterprise)>();

        foreach (var e in _db.LoadAllEmails())
        {
            // 仅当主题本身用标签（方括弧）标注了企业，才作为学习依据
            var parsed = SubjectParser.Parse(e.Subject);
            if (parsed == null || parsed.Enterprise.Length == 0) continue;

            foreach (var domain in DomainsOf(e))
            {
                if (serviceDomains.Contains(domain)) continue;                 // 本机/客服域名
                if (IsVendorDomain(domain)) continue;                          // 厂家域名（zoho/manageengine）不是客户
                if (mappings.ContainsKey(domain)) continue;                    // 已有映射不覆盖
                if (toAdd.Any(x => string.Equals(x.Domain, domain, StringComparison.OrdinalIgnoreCase)))
                    continue;                                                  // 已排队不重复
                toAdd.Add((domain, parsed.Enterprise));
            }
        }

        if (toAdd.Count == 0) return;
        foreach (var (domain, enterprise) in toAdd)
            mappings[domain] = enterprise;
        SaveConfig(_config); // 持久化，可在 设置→域名→企业 中查看/删除
    }

    /// <summary>本机账号 + 关注的客服邮箱域名（这些域名不可能是客户企业域名）。</summary>
    private HashSet<string> GetServiceDomains()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDomain(set, _config.ImapUsername);
        foreach (var addr in _config.MonitoredAddresses) AddDomain(set, addr);
        return set;
    }

    private static void AddDomain(HashSet<string> set, string address)
    {
        var at = address.LastIndexOf('@');
        if (at >= 0 && at < address.Length - 1) set.Add(address[(at + 1)..].Trim());
    }

    /// <summary>取一封邮件 发件/收件/抄送 地址的所有域名（去重、小写）。</summary>
    private static List<string> DomainsOf(EmailMessage e)
    {
        var list = new List<string>();
        void Add(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            var at = address.LastIndexOf('@');
            if (at < 0 || at == address.Length - 1) return;
            var domain = address[(at + 1)..].Trim().ToLowerInvariant();
            if (domain.Length == 0 || list.Contains(domain, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(domain);
        }
        Add(e.FromAddress);
        foreach (var a in SplitList(e.ToAddresses)) Add(a);
        foreach (var a in SplitList(e.CcAddresses)) Add(a);
        return list;
    }

    /// <summary>把 域名→企业 映射从设置字符串解析为字典。</summary>
    private static Dictionary<string, string> ParseMappings(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return dict;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            if (parsed != null)
                foreach (var kv in parsed)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        dict[kv.Key.Trim()] = kv.Value.Trim();
        }
        catch { }
        return dict;
    }

    /// <summary>对主题未按约定标注 产品/客户 的邮件，用 AI 从主题与邮箱地址中分析补齐。</summary>
    private async Task EnrichMissingMetaAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.DeepSeekApiKey)) return;
        var pending = _db.LoadAllEmails()
            .Where(e => string.IsNullOrEmpty(e.Product) || string.IsNullOrEmpty(e.Enterprise))
            .ToList();
        if (pending.Count == 0) return;
        using var ai = new DeepSeekService(_config);
        int done = 0;
        var tasks = pending.Select(async e =>
        {
            ct.ThrowIfCancellationRequested();
            var r = await ai.ExtractMetaAsync(e);
            if (r != null)
            {
                var product = string.IsNullOrEmpty(e.Product) ? r.Value.Product : e.Product;
                var enterprise = string.IsNullOrEmpty(e.Enterprise) ? r.Value.Enterprise : e.Enterprise;
                enterprise = ResolveEnterpriseName(enterprise); // 英文企业名对照 域名→企业 映射表翻译成中文
                _db.UpdateEmailMeta(e.Id, new ParsedSubject(e.TicketNumber, product, enterprise, e.FaultDescription));
            }
            var d = Interlocked.Increment(ref done);
            progress?.Report($"正在分析产品/客户… {d}/{pending.Count}");
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>清空下载的本地邮件与工单（保留全部配置）。</summary>
    public void ClearAllData() => _db.ClearAllData();

    /// <summary>获取库中已有的产品/客户列表，供手工指定时选择。</summary>
    public List<string> GetKnownProducts() => _db.GetKnownProducts();
    public List<string> GetKnownEnterprises() => _db.GetKnownEnterprises();

    /// <summary>手工指定某封邮件及其同线程其他邮件（其余只填空缺）的 产品/客户，使整棵线程归位。</summary>
    public void SetEmailMeta(long id, string product, string enterprise)
    {
        _db.SetEmailAndThreadMeta(id, product, enterprise);
        RebuildThreads();
    }

    /// <summary>手工设置线程状态（清空 AI 总结，避免与状态矛盾）。</summary>
    public void SetThreadStatus(long threadId, string status)
    {
        _db.UpdateThreadStatus(threadId, status, "");
    }

    /// <summary>立即用 AI 为指定线程重新生成状态与总结，返回 (状态, 总结)；失败返回 null。</summary>
    public async Task<(string Status, string Summary)?> RegenerateThreadStatusAsync(
        long threadId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.DeepSeekApiKey)) return null;
        var thread = _db.LoadThreads().FirstOrDefault(t => t.Id == threadId);
        if (thread == null) return null;
        using var ai = new DeepSeekService(_config);
        var r = await ai.SummarizeThreadAsync(thread);
        if (r == null) return null;
        _db.UpdateThreadStatus(thread.Id, r.Value.Status, r.Value.Summary);
        progress?.Report("正在总结工单状态…");
        return r;
    }

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
        var firstIds = _db.GetFirstEmailIdsPerThread();
        // 非首封：AI 提炼标题；首封：仅当主题为英文时 AI 翻译成中文（中文首封保留原主题）
        var toTitle = pending.Where(e =>
            !firstIds.Contains(e.Id) ||
            (!string.IsNullOrEmpty(e.Subject) && !SubjectParser.ContainsCjk(e.Subject))).ToList();
        if (toTitle.Count == 0) return;
        using var ai = new DeepSeekService(_config);
        int done = 0;
        var tasks = toTitle.Select(async e =>
        {
            ct.ThrowIfCancellationRequested();
            var title = await ai.SummarizeTitleAsync(e);
            if (!string.IsNullOrWhiteSpace(title))
                _db.UpdateAiTitle(e.Id, title.Trim().Trim('"', '「', '」'));
            var d = Interlocked.Increment(ref done);
            progress?.Report($"正在生成标题… {d}/{toTitle.Count}");
        });
        await Task.WhenAll(tasks);
    }

    private async Task GenerateStatusesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.DeepSeekApiKey)) return;
        var threads = _db.LoadThreads();
        if (threads.Count == 0) return;

        // 只总结 有新邮件 的线程（或从未总结过的）：没有新增邮件的线程沿用上次总结，避免对每棵线程重复调用 AI
        var lastSummarized = DateTimeOffset.Now; // 无记录时视为“从现在起”，避免首次启用时重刷已有状态
        if (DateTimeOffset.TryParse(_db.GetSetting("last_status_summary_at"), out var ts))
            lastSummarized = ts;

        var pending = threads
            .Where(t => string.IsNullOrEmpty(t.Status) || t.LastActivity > lastSummarized)
            .ToList();
        if (pending.Count == 0) return;

        using var ai = new DeepSeekService(_config);
        int done = 0;
        var tasks = pending.Select(async t =>
        {
            ct.ThrowIfCancellationRequested();
            var r = await ai.SummarizeThreadAsync(t);
            if (r != null)
                _db.UpdateThreadStatus(t.Id, r.Value.Status, r.Value.Summary);
            var d = Interlocked.Increment(ref done);
            progress?.Report($"正在总结工单状态… {d}/{pending.Count}");
        });
        await Task.WhenAll(tasks);
        _db.SetSetting("last_status_summary_at", DateTimeOffset.Now.ToString("o"));
    }

    // ================= helpers =================

    private static int IntOr(string s, int def) => int.TryParse(s, out var v) ? v : def;
    private static bool BoolOr(string s, bool def) => bool.TryParse(s, out var v) ? v : def;
    private static string StrOr(string s, string def) => string.IsNullOrEmpty(s) ? def : s;

    private static List<string> SplitList(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
