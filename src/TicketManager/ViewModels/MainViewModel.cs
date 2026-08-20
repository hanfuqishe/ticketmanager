using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.ViewModels;

public enum TreeSortMode { Time, Product, Customer }

public record SortOption(string Label, TreeSortMode Mode);

public class MainViewModel : ViewModelBase
{
    private readonly WorkflowService _workflow;
    private List<TicketThread> _threads = new();
    private TreeSortMode _sortMode = TreeSortMode.Time;
    private SortOption _selectedSort = null!;

    public ObservableCollection<CustomerGroupViewModel> Customers { get; } = new();

    public ObservableCollection<SortOption> SortOptions { get; } = new()
    {
        new("按时间（最新在前）", TreeSortMode.Time),
        new("按产品名称", TreeSortMode.Product),
        new("按客户名称", TreeSortMode.Customer),
    };

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value == null || !Set(ref _selectedSort, value)) return;
            _sortMode = value.Mode;
            RebuildTree();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                OnPropertyChanged(nameof(BusyVisibility));
        }
    }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    // ---- 进度条（百分比）----
    private double _progressValue;
    /// <summary>当前进度百分比 0-100（有具体进度时）。</summary>
    public double ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }

    private bool _progressIndeterminate = true;
    /// <summary>无具体进度（扫描/等待等）时为 true，进度条用持续滚动模式。</summary>
    public bool ProgressIndeterminate { get => _progressIndeterminate; set => Set(ref _progressIndeterminate, value); }

    /// <summary>进度条旁的百分比文字（如 “35%”）；无具体进度时为空。</summary>
    public string ProgressPercentText => ProgressIndeterminate ? "" : $"{Math.Round(ProgressValue)}%";

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _searchText = "";
    /// <summary>检索关键字：只匹配线索根邮件的主题（原主题与 AI 翻译），变化时即时过滤重建树。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                RebuildTree();
        }
    }

    /// <summary>线索根邮件主题是否匹配检索关键字（留空显示全部）。</summary>
    private bool MatchesSearch(TicketThread t)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var q = _searchText.Trim();
        var root = t.Emails.Count > 0 ? t.Emails[0] : t.DisplayRoots.FirstOrDefault()?.Email;
        if (root == null) return false;
        return root.Subject.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               root.AiTitle.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private ThreadViewModel? _selectedThread;
    public ThreadViewModel? SelectedThread
    {
        get => _selectedThread;
        set
        {
            if (Set(ref _selectedThread, value))
            {
                OnPropertyChanged(nameof(SelectedThreadHeader));
                OnPropertyChanged(nameof(SelectedThreadSummary));
                OnPropertyChanged(nameof(SelectedSummaryBorder));
                OnPropertyChanged(nameof(SelectedSummaryBackground));
            }
        }
    }

    private EmailNodeViewModel? _selectedEmail;
    public EmailNodeViewModel? SelectedEmail
    {
        get => _selectedEmail;
        set
        {
            if (Set(ref _selectedEmail, value))
            {
                OnPropertyChanged(nameof(SelectedEmailTitle));
                OnPropertyChanged(nameof(SelectedEmailMeta));
                OnPropertyChanged(nameof(SelectedEmailBody));
                OnPropertyChanged(nameof(EmailBodyDisplay));
                ResetTranslation();
            }
        }
    }

    public string SelectedThreadHeader => SelectedThread?.Header ?? "未选择工单";
    public string SelectedThreadSummary =>
        SelectedThread?.Summary ?? "从左侧选择一条工单线索，查看邮件往来与智能总结。";

    /// <summary>AI 总结框配色（随工单状态变化）。</summary>
    public Brush SelectedSummaryBorder => SelectedThread?.SummaryBorder ?? Brushes.Gray;
    public Brush SelectedSummaryBackground => SelectedThread?.SummaryBackground ?? Brushes.LightGray;
    public string SelectedEmailTitle => SelectedEmail?.Title ?? "";
    public string SelectedEmailMeta => SelectedEmail?.Meta ?? "";
    public string SelectedEmailBody => FilterBlankLines(SelectedEmail?.Body ?? "");

    // ---- AI 翻译 ----
    private bool _showTranslation;
    public bool ShowTranslation
    {
        get => _showTranslation;
        set
        {
            if (Set(ref _showTranslation, value))
            {
                OnPropertyChanged(nameof(EmailBodyDisplay));
                OnPropertyChanged(nameof(TranslateButtonText));
            }
        }
    }

    private string _emailTranslation = "";
    public string EmailTranslation { get => _emailTranslation; set => Set(ref _emailTranslation, value); }

    /// <summary>正文显示：翻译后且已点翻译 → 译文；否则原文。</summary>
    public string EmailBodyDisplay =>
        ShowTranslation && !string.IsNullOrEmpty(EmailTranslation) ? EmailTranslation : SelectedEmailBody;
    public string TranslateButtonText => ShowTranslation ? "显示原文" : "AI 翻译";

    /// <summary>正文是否以英文为主（无中文且含英文单词），用于显示翻译按钮。</summary>
    public bool IsEmailEnglish
    {
        get
        {
            var b = SelectedEmailBody;
            if (string.IsNullOrWhiteSpace(b)) return false;
            return !SubjectParser.ContainsCjk(b) && Regex.IsMatch(b, @"[A-Za-z]{3,}");
        }
    }
    public Visibility TranslateButtonVisibility => IsEmailEnglish ? Visibility.Visible : Visibility.Collapsed;

    private void ResetTranslation()
    {
        ShowTranslation = false;
        // 从数据库加载该邮件的已缓存翻译（持久化，重启/切换邮件后无需重新调用 AI）
        var id = SelectedEmail?.Email.Id ?? 0;
        EmailTranslation = id > 0 ? _workflow.GetEmailTranslation(id) : "";
        OnPropertyChanged(nameof(IsEmailEnglish));
        OnPropertyChanged(nameof(TranslateButtonVisibility));
    }

    private async Task TranslateEmailAsync()
    {
        if (ShowTranslation) { ShowTranslation = false; return; } // 再点一次切回原文
        var body = SelectedEmailBody;
        if (string.IsNullOrWhiteSpace(body))
        {
            StatusText = "没有可翻译的邮件正文";
            return;
        }
        // 切回原文后再点翻译：已有缓存则直接重新显示，不重复调用 AI
        if (!string.IsNullOrEmpty(EmailTranslation))
        {
            ShowTranslation = true;
            StatusText = "已显示翻译结果";
            return;
        }
        if (string.IsNullOrEmpty(_workflow.Config.DeepSeekApiKey))
        {
            StatusText = "未配置 DeepSeek API Key，无法翻译";
            return;
        }
        StatusText = "正在翻译邮件…";
        using var ai = new DeepSeekService(_workflow.Config);
        var translated = await ai.TranslateTextAsync(body);
        if (string.IsNullOrWhiteSpace(translated))
        {
            StatusText = "翻译失败（DeepSeek 调用出错）";
            return;
        }
        EmailTranslation = translated;
        var emailId = SelectedEmail?.Email.Id;
        if (emailId is > 0) _workflow.SetEmailTranslation(emailId.Value, translated); // 持久化缓存
        ShowTranslation = true;
        StatusText = "翻译完成";
    }

    public string DbPath => App.Db.DbPath;

    public ICommand SyncCommand { get; }
    public ICommand StopSyncCommand { get; }
    public ICommand TranslateEmailCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ClearDataCommand { get; }

    public MainViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        _selectedSort = SortOptions[0];
        SyncCommand = new RelayCommand(async _ => await SyncAsync());
        StopSyncCommand = new RelayCommand(_ => StopSync());
        TranslateEmailCommand = new RelayCommand(async _ => await TranslateEmailAsync());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        ClearDataCommand = new RelayCommand(async _ => await ClearAllDataAsync());
    }

    // ---- 多线索选择（Ctrl/Shift 多选）与批量设置产品/客户 ----
    private readonly HashSet<long> _selectedThreadIds = new();
    public IReadOnlyCollection<long> SelectedThreadIds => _selectedThreadIds;
    public int SelectedCount => _selectedThreadIds.Count;
    public bool HasSelection => _selectedThreadIds.Count > 0;

    /// <summary>应用新的选中线索集合（由 TreeView 的 Ctrl/Shift 选择逻辑计算），并刷新高亮。</summary>
    public void ApplySelection(IReadOnlyCollection<long> ids)
    {
        _selectedThreadIds.Clear();
        foreach (var id in ids) _selectedThreadIds.Add(id);
        UpdateSelectionHighlight();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>清空多选（并刷新高亮）。</summary>
    public void ClearSelection()
    {
        _selectedThreadIds.Clear();
        UpdateSelectionHighlight();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>把当前选中集合反映到树上（根线索高亮）。树重建后也会调用以恢复高亮。</summary>
    public void UpdateSelectionHighlight()
    {
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var n in prod.Threads)
                    n.IsMultiSelected = _selectedThreadIds.Contains(n.Email.ThreadId);
    }

    /// <summary>对一组线索统一设置 产品/客户（只覆盖非空字段），并重建线程表使分组刷新。</summary>
    public void SetMetaForThreads(IEnumerable<long> threadIds, string product, string enterprise)
    {
        foreach (var tid in threadIds)
            _workflow.SetThreadMeta(tid, product, enterprise);
        _workflow.RebuildThreads(); // 重建线程表（按新产品/客户归组），否则界面分组不会刷新
    }

    /// <summary>取指定线程集合的根邮件 Id（邮件 Id 在线程重建后保持稳定，用于重新定位选中）。</summary>
    public List<long> GetRootEmailIds(IEnumerable<long> threadIds)
    {
        var set = threadIds.ToHashSet();
        var result = new List<long>();
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var n in prod.Threads)
                    if (n.IsRoot && set.Contains(n.Email.ThreadId))
                        result.Add(n.Email.Id);
        return result;
    }

    /// <summary>按根邮件 Id 重新选中对应线索（线程重建后 ThreadId 变化，用稳定的邮件 Id 定位）。</summary>
    public void ReselectByRootEmailIds(IEnumerable<long> rootEmailIds)
    {
        var idSet = rootEmailIds.ToHashSet();
        var ids = new List<long>();
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var n in prod.Threads)
                    if (n.IsRoot && idSet.Contains(n.Email.Id))
                        ids.Add(n.Email.ThreadId);
        ApplySelection(ids);
    }

    private CancellationTokenSource? _autoSyncCts;
    private CancellationTokenSource? _manualSyncCts; // 手动同步的取消源（“停止同步”用它）

    /// <summary>停止正在进行的（手动）同步。</summary>
    private void StopSync()
    {
        _manualSyncCts?.Cancel();
        StatusText = "正在停止同步…";
    }

    /// <summary>创建进度报告器：更新状态栏文字，并从 “x/y” 消息解析百分比驱动进度条。</summary>
    private IProgress<string> CreateProgress() => new Progress<string>(s =>
    {
        StatusText = s;
        UpdateProgress(s);
    });

    /// <summary>从进度消息（如 “正在下载收件箱… 12/34”）解析出百分比；无具体进度时用不确定模式。</summary>
    private void UpdateProgress(string s)
    {
        var m = Regex.Match(s, @"(\d+)\s*/\s*(\d+)");
        if (m.Success && double.TryParse(m.Groups[2].Value, out var total) && total > 0 &&
            double.TryParse(m.Groups[1].Value, out var done))
        {
            ProgressValue = Math.Clamp(done / total * 100, 0, 100);
            ProgressIndeterminate = false;
        }
        else
        {
            ProgressValue = 0;
            ProgressIndeterminate = true;
        }
        OnPropertyChanged(nameof(ProgressPercentText));
    }

    /// <summary>把连续空行（含 \xa0 等不可见空白）压缩为单个空行，过滤多余空行。</summary>
    private static string FilterBlankLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // 统一换行符（兼容 \r\n / \r / \n 混用）
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        // [^\S\r\n] = 空白但排除换行（含 \xa0、全角空格等）；2 个以上连续换行 → 单个空行
        return Regex.Replace(s, @"\n[^\S\r\n]*\n[^\S\r\n]*(?:\n[^\S\r\n]*)+", "\n\n");
    }

    /// <summary>启动自动收取。REST 模式检查 Zoho 配置，否则检查 IMAP 配置；按当前配置决定是否开启。</summary>
    public void StartAutoSync()
    {
        StopAutoSync();
        if (!_workflow.Config.EnableAutoSync) return;
        bool useZoho = !string.IsNullOrEmpty(_workflow.Config.ZohoClientId) &&
                       !string.IsNullOrEmpty(_workflow.Config.ZohoRefreshToken);
        if (useZoho)
        {
            if (string.IsNullOrWhiteSpace(_workflow.Config.ZohoRefreshToken)) return;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_workflow.Config.ImapUsername) ||
                string.IsNullOrWhiteSpace(_workflow.Config.ImapHost)) return;
        }

        _autoSyncCts = new CancellationTokenSource();
        var ct = _autoSyncCts.Token;
        var progress = CreateProgress();
        _ = Task.Run(async () =>
        {
            try
            {
                await _workflow.RunAutoSyncLoopAsync(
                    onSynced: _ => Application.Current.Dispatcher.Invoke(() =>
                    {
                        _threads = _workflow.LoadThreads();
                        RebuildTree();
                    }),
                    progress, ct);
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    /// <summary>停止自动收取。</summary>
    public void StopAutoSync()
    {
        _autoSyncCts?.Cancel();
        _autoSyncCts?.Dispose();
        _autoSyncCts = null;
    }

    public void Load()
    {
        _workflow.LoadConfig(); // 确保配置已加载（发件人配色需要客服邮箱与自身邮箱）
        _threads = _workflow.LoadThreads();
        RebuildTree();
        StatusText = $"已加载 {_threads.Count} 条工单线索";
    }

    /// <summary>
    /// 启动后：延迟片刻再自动同步一次（给用户留出"清空本地数据"的时间），
    /// 随后进入自动收取新邮件模式（若已启用）。延迟期间点清空会取消本次自动同步。
    /// </summary>
    public async void AutoSyncAndListen()
    {
        var cts = new CancellationTokenSource();
        _autoSyncCts = cts;
        var ct = cts.Token;
        try
        {
            // 首次自动同步延迟 10 秒：此时若用户点击"清空本地数据"，
            // ClearAllDataAsync 里的 StopAutoSync 会取消本延迟与同步。
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (ct.IsCancellationRequested) return;
            await SyncAsync(ct);
            if (ct.IsCancellationRequested) return;
            StartAutoSync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>重新加载线程并重建树（手工设置产品/客户后刷新）。</summary>
    public void Reload()
    {
        _threads = _workflow.LoadThreads();
        RebuildTree();
    }

    /// <summary>手工设置线程状态：更新数据库 + 内存线程对象，并就地刷新根邮件显示（不重建树，避免闪烁）。</summary>
    public void SetThreadStatus(long threadId, string status)
    {
        _workflow.SetThreadStatus(threadId, status);
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                {
                    if (root.Email.ThreadId != threadId) continue;
                    root.ThreadOwner.Thread.Status = status;
                    root.ThreadOwner.Thread.StatusSummary = "";
                    root.RefreshThreadInfo();
                    break;
                }
        // 若右侧详情面板显示的是该线程，同步刷新标题/总结/状态框配色
        if (SelectedThread != null && SelectedThread.Thread.Id == threadId)
        {
            OnPropertyChanged(nameof(SelectedThreadHeader));
            OnPropertyChanged(nameof(SelectedThreadSummary));
            OnPropertyChanged(nameof(SelectedSummaryBorder));
            OnPropertyChanged(nameof(SelectedSummaryBackground));
        }
    }

    /// <summary>立即用 AI 重新生成某线程的状态/总结并就地刷新显示（不重建树）。</summary>
    public async Task RegenerateThreadStatusAsync(long threadId)
    {
        StatusText = "正在用 AI 总结该工单…";
        var r = await _workflow.RegenerateThreadStatusAsync(threadId);
        if (r == null)
        {
            StatusText = "AI 总结失败（可能未配置 API Key 或调用出错）";
            return;
        }
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                {
                    if (root.Email.ThreadId != threadId) continue;
                    root.ThreadOwner.Thread.Status = r.Value.Status;
                    root.ThreadOwner.Thread.StatusSummary = r.Value.Summary;
                    root.RefreshThreadInfo();
                    break;
                }
        if (SelectedThread != null && SelectedThread.Thread.Id == threadId)
        {
            OnPropertyChanged(nameof(SelectedThreadHeader));
            OnPropertyChanged(nameof(SelectedThreadSummary));
            OnPropertyChanged(nameof(SelectedSummaryBorder));
            OnPropertyChanged(nameof(SelectedSummaryBackground));
        }
        StatusText = "AI 总结已更新";
    }

    private async Task SyncAsync(CancellationToken externalCt = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        // 链接外部令牌（自动同步的取消）与手动取消源（停止同步），两者任一取消都会中断本次同步
        _manualSyncCts?.Dispose();
        _manualSyncCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _manualSyncCts.Token;
        try
        {
            var progress = CreateProgress();
            var n = await _workflow.SyncAndProcessAsync(progress, ct);
            _threads = _workflow.LoadThreads();
            RebuildTree();
            StatusText = $"同步完成：新增 {n} 封邮件，共 {_threads.Count} 条线索";
        }
        catch (OperationCanceledException)
        {
            // 同步被“停止同步”或自动同步取消：保留已同步的邮件，重建线程刷新界面
            _threads = _workflow.LoadThreads();
            RebuildTree();
            StatusText = "同步已停止（已保留同步到的邮件）";
        }
        catch (Exception ex)
        {
            // 已入库的邮件仍在：重建线程并刷新界面，避免“同步失败 = 数据全部丢失”的错觉
            _threads = _workflow.LoadThreads();
            RebuildTree();
            StatusText = $"同步失败：{ex.Message}（已保留已同步的邮件）";
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = true;
            ProgressValue = 0;
            OnPropertyChanged(nameof(ProgressPercentText));
            _manualSyncCts?.Dispose();
            _manualSyncCts = null;
        }
    }

    private void OpenSettings()
    {
        var win = new Views.SettingsWindow(_workflow)
        {
            Owner = Application.Current.MainWindow
        };
        // 仅当点击「保存」才刷新数据并按最新设置重启自动收取；「取消」不触发任何同步
        if (win.ShowDialog() == true)
        {
            Load();
            StartAutoSync();
        }
    }

    private async Task ClearAllDataAsync()
    {
        if (IsBusy)
        {
            MessageBox.Show("正在同步中，请稍候片刻再试。", "清空本地邮件数据",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            "确定要清空本地邮件数据吗？此操作不可恢复！\n\n" +
            "将删除：已下载的所有邮件和工单线索，并重置同步状态（下次同步重新拉取最近 7 天的邮件）。\n\n" +
            "邮箱、DeepSeek、代理等设置会保留。",
            "清空本地邮件数据", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        StopAutoSync(); // 先停掉后台自动同步，避免清空后又被自动拉回
        IsBusy = true;
        try
        {
            await Task.Yield(); // 让忙碌指示先刷新
            await _workflow.ClearAllDataAsync();
            SelectedThread = null;
            SelectedEmail = null;
            _threads = new List<TicketThread>();
            RebuildTree();
            StatusText = "已清空本地邮件数据，设置已保留，可点击同步重新拉取";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SelectThread(ThreadViewModel tv)
    {
        SelectedThread = tv;
        SelectedEmail = null;
    }

    public void SelectEmail(EmailNodeViewModel ev)
    {
        SelectedEmail = ev;
        SelectedThread = ev.ThreadOwner;
    }

    private void RebuildTree()
    {
        BuildTree(_threads, _sortMode);
        if (_selectedThreadIds.Count > 0) UpdateSelectionHighlight(); // 重建后恢复多选高亮
    }

    private void BuildTree(List<TicketThread> threads, TreeSortMode mode)
    {
        Customers.Clear();
        var supportAddresses = _workflow.Config.MonitoredAddresses;
        var selfAddress = _workflow.Config.ImapUsername;
        var customerGroups = threads
            .Where(MatchesSearch)
            .GroupBy(t => string.IsNullOrEmpty(t.Enterprise) ? "未分类客户" : t.Enterprise);

        var orderedCustomers = mode switch
        {
            TreeSortMode.Time => customerGroups.OrderByDescending(g => g.Max(t => t.LastActivity)),
            _ => customerGroups.OrderBy(g => g.Key, StringComparer.Ordinal)
        };

        foreach (var cg in orderedCustomers)
        {
            var customer = new CustomerGroupViewModel(cg.Key);
            var productGroups = cg
                .GroupBy(t => string.IsNullOrEmpty(t.Product) ? "未分类产品" : t.Product);

            var orderedProducts = mode switch
            {
                TreeSortMode.Time => productGroups.OrderByDescending(g => g.Max(t => t.LastActivity)),
                _ => productGroups.OrderBy(g => g.Key, StringComparer.Ordinal)
            };

            foreach (var pg in orderedProducts)
            {
                var product = new ProductGroupViewModel(pg.Key);
                // 同一产品下的所有邮件树一律按最后更新时间倒序（与全局排序模式无关）
                var orderedThreads = pg.OrderByDescending(t => t.LastActivity);
                foreach (var t in orderedThreads)
                {
                    // 工单层并入根邮件节点：状态/工单号/总结显示在根邮件上，不再有独立工单行
                    var owner = new ThreadViewModel(t, supportAddresses, selfAddress);
                    foreach (var root in owner.Children)
                        product.Threads.Add(root);
                }
                customer.Products.Add(product);
            }
            Customers.Add(customer);
        }
    }
}
