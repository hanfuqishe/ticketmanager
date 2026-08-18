using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
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

    public void Load()
    {
        _workflow.LoadConfig(); // 确保配置已加载（发件人配色需要客服邮箱与自身邮箱）
        _threads = _workflow.LoadThreads();
        RebuildTree();
        StatusText = $"已加载 {_threads.Count} 条工单线索";
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
                var orderedThreads = mode switch
                {
                    TreeSortMode.Time => pg.OrderByDescending(t => t.LastActivity),
                    TreeSortMode.Product => pg.OrderBy(t => t.Product, StringComparer.Ordinal)
                                              .ThenBy(t => t.TicketNumber, StringComparer.Ordinal)
                                              .ThenByDescending(t => t.LastActivity),
                    _ => pg.OrderBy(t => t.Enterprise, StringComparer.Ordinal)
                            .ThenBy(t => t.TicketNumber, StringComparer.Ordinal)
                            .ThenByDescending(t => t.LastActivity),
                };
                foreach (var t in orderedThreads)
                    product.Threads.Add(new ThreadViewModel(t, supportAddresses, selfAddress));
                customer.Products.Add(product);
            }
            Customers.Add(customer);
        }
    }
}
