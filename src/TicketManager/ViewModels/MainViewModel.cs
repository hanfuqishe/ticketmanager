using System.Collections.ObjectModel;
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
    public string SelectedEmailBody => SelectedEmail?.Body ?? "";

    public string DbPath => App.Db.DbPath;

    public ICommand SyncCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ClearDataCommand { get; }

    public MainViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        _selectedSort = SortOptions[0];
        SyncCommand = new RelayCommand(async _ => await SyncAsync());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        ClearDataCommand = new RelayCommand(async _ => await ClearAllDataAsync());
    }

    private CancellationTokenSource? _autoSyncCts;

    /// <summary>启动自动收取（IMAP IDLE 监听新邮件，到达即自动同步）。按当前配置决定是否开启。</summary>
    public void StartAutoSync()
    {
        StopAutoSync();
        if (!_workflow.Config.EnableAutoSync) return;
        if (string.IsNullOrWhiteSpace(_workflow.Config.ImapUsername) ||
            string.IsNullOrWhiteSpace(_workflow.Config.ImapHost)) return;

        _autoSyncCts = new CancellationTokenSource();
        var ct = _autoSyncCts.Token;
        var progress = new Progress<string>(s => StatusText = s);
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

    /// <summary>启动时：先自动同步一次，随后进入自动收取新邮件模式（若已启用）。</summary>
    public async void AutoSyncAndListen()
    {
        await SyncAsync();
        StartAutoSync();
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

    private async Task SyncAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            var n = await _workflow.SyncAndProcessAsync(progress);
            _threads = _workflow.LoadThreads();
            RebuildTree();
            StatusText = $"同步完成：新增 {n} 封邮件，共 {_threads.Count} 条线索";
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
        }
    }

    private void OpenSettings()
    {
        var win = new Views.SettingsWindow(_workflow)
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
        Load();
        StartAutoSync(); // 按最新设置重启自动收取
    }

    private async Task ClearAllDataAsync()
    {
        if (IsBusy) return;
        var confirm = MessageBox.Show(
            "确定要清空本地邮件数据吗？此操作不可恢复！\n\n" +
            "将删除：已下载的所有邮件和工单线索，并重置同步状态（下次同步重新拉取最近 7 天的邮件）。\n\n" +
            "邮箱、DeepSeek、代理等设置会保留。",
            "清空本地邮件数据", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            await Task.Yield(); // 让忙碌指示先刷新
            _workflow.ClearAllData();
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

    private void RebuildTree() => BuildTree(_threads, _sortMode);

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
