using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    private ObservableCollection<CustomerGroupViewModel> _customers = new();
    public ObservableCollection<CustomerGroupViewModel> Customers => _customers;

    /// <summary>一次性替换客户分组集合（避免 Clear+逐条 Add 导致的界面闪烁/空白）。</summary>
    private void ReplaceCustomers(IEnumerable<CustomerGroupViewModel> items)
    {
        _customers = new ObservableCollection<CustomerGroupViewModel>(items);
        OnPropertyChanged(nameof(Customers));
    }

    public ObservableCollection<SortOption> SortOptions { get; } = new()
    {
        new("按时间（最新在前）", TreeSortMode.Time),
        new("按产品名称", TreeSortMode.Product),
        new("按客户名称", TreeSortMode.Customer),
    };

    private int _expandDepth = 3;
    /// <summary>默认展开层次：1=只显示用户名称，2=显示到产品名称，3=显示到线索首邮件（默认），4=显示所有邮件。</summary>
    public int ExpandDepth
    {
        get => _expandDepth;
        set
        {
            if (!Set(ref _expandDepth, Math.Clamp(value, 1, 4))) return;
            OnPropertyChanged(nameof(IsExpandDepth1));
            OnPropertyChanged(nameof(IsExpandDepth2));
            OnPropertyChanged(nameof(IsExpandDepth3));
            OnPropertyChanged(nameof(IsExpandDepth4));
            _workflow.SetExpandDepth(_expandDepth); // 持久化，重启后保留
            ApplyExpandDepthToVm(); // 只更新现有 VM 的展开标志（不重建整棵树，更快）
        }
    }
    public bool IsExpandDepth1 => ExpandDepth == 1;
    public bool IsExpandDepth2 => ExpandDepth == 2;
    public bool IsExpandDepth3 => ExpandDepth == 3;
    public bool IsExpandDepth4 => ExpandDepth == 4;

    /// <summary>按当前展开层次更新树中所有节点的 ExpandedByDefault（复用现有 VM，避免重建树导致卡顿）。</summary>
    public void ApplyExpandDepthToVm()
    {
        foreach (var cust in Customers)
        {
            cust.ExpandedByDefault = ExpandDepth >= 2;
            foreach (var prod in cust.Products)
            {
                prod.ExpandedByDefault = ExpandDepth >= 3;
                foreach (var root in prod.Threads)
                    SetEmailExpandDepth(root, ExpandDepth);
            }
        }
    }

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

    private bool _newMailOnly;
    /// <summary>“仅新邮件”过滤：只显示含有新同步邮件的线索（切换时即时重建树）。</summary>
    public bool NewMailOnly
    {
        get => _newMailOnly;
        set
        {
            if (Set(ref _newMailOnly, value))
                RebuildTreeWithBusyCursor();
        }
    }

    private bool _starredOnly;
    /// <summary>“仅星标”过滤：只显示含有星标邮件的线索（切换时即时重建树）。</summary>
    public bool StarredOnly
    {
        get => _starredOnly;
        set
        {
            if (Set(ref _starredOnly, value))
                RebuildTreeWithBusyCursor();
        }
    }

    /// <summary>在忙碌光标下重建树：过滤切换重建可能耗时，先让鼠标变为等待状再执行（
    /// 用 Background 优先级延迟执行，确保等待光标先渲染出来；无 WPF 环境时回退为同步重建）。</summary>
    private void RebuildTreeWithBusyCursor()
    {
        if (Application.Current == null) { RebuildTree(); return; }
        Mouse.OverrideCursor = Cursors.Wait;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                RebuildTree();
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }));
    }

    /// <summary>
    /// 线索是否匹配检索关键字（留空显示全部）。
    /// 匹配范围：线索工单号 + 线程内任意邮件的主题/AI 标题。
    /// 根邮件（首封报障）往往不含工单号（工单号在客服后续回复才加入主题），因此不能只匹配根邮件主题。
    /// </summary>
    private bool MatchesSearch(TicketThread t)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var q = _searchText.Trim();
        if (t.TicketNumber.Contains(q, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var e in t.Emails)
            if (e.Subject.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.AiTitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>线索内是否存在新同步的邮件（“仅新邮件”过滤依据）。</summary>
    private static bool HasNewMail(TicketThread t) => t.Emails.Any(e => e.IsNew);

    /// <summary>线索是否匹配当前全部过滤（检索关键字 + 仅新邮件 + 仅星标）。
    /// 建树（RebuildTree）与同步后增量合并（MergeThreads）必须用同一套条件，
    /// 否则同步会把不匹配过滤的线索也插进树，表现为“同步后退出过滤状态”。</summary>
    private bool MatchesFilter(TicketThread t)
        => MatchesSearch(t) && (!_newMailOnly || HasNewMail(t)) && (!_starredOnly || t.Emails.Any(e => e.Starred));

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
                OnPropertyChanged(nameof(SelectedStatusReason));
                OnPropertyChanged(nameof(SelectedStatusReasonDisplay));
                OnPropertyChanged(nameof(HasStatusReason));
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

    /// <summary>选中线索的手工状态理由（无则空串，右侧总结区显示）。</summary>
    public string SelectedStatusReason => SelectedThread?.Thread.StatusReason ?? "";

    /// <summary>手工状态理由显示文本（带前缀；无理由时为空）。</summary>
    public string SelectedStatusReasonDisplay =>
        string.IsNullOrEmpty(SelectedStatusReason) ? "" : $"📝 手工设置理由：{SelectedStatusReason}";

    /// <summary>是否存在手工状态理由（控制右侧显示）。</summary>
    public bool HasStatusReason => !string.IsNullOrEmpty(SelectedStatusReason);

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
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait; // AI 翻译较耗时，先显示忙碌光标
        try
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
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    public string DbPath => App.Db.DbPath;

    public ICommand SyncCommand { get; }
    public ICommand StopSyncCommand { get; }
    public ICommand TranslateEmailCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SignaturesCommand { get; }
    public ICommand ClearDataCommand { get; }
    public ICommand JumpToNewMailCommand { get; }
    public ICommand SubmitTicketCommand { get; }
    public ICommand RegenerateAiSummaryCommand { get; }
    public ICommand FontSettingsCommand { get; }

    /// <summary>跳转新邮件的请求（由主窗口订阅执行 TreeView 滚动选中）。</summary>
    public event Action? JumpToNewMailRequested;

    /// <summary>增量合并后已恢复选中（节点可能被替换/移动），主窗口据此在 TreeView 中重新高亮选中。</summary>
    public event Action? SelectionRestoredAfterMerge;

    public MainViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        _selectedSort = SortOptions[0];
        SyncCommand = new RelayCommand(async _ => await SyncAsync());
        StopSyncCommand = new RelayCommand(_ => StopSync());
        TranslateEmailCommand = new RelayCommand(async _ => await TranslateEmailAsync());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        SignaturesCommand = new RelayCommand(_ => OpenSignatures());
        ClearDataCommand = new RelayCommand(async _ => await ClearAllDataAsync());
        JumpToNewMailCommand = new RelayCommand(_ => JumpToNewMailRequested?.Invoke());
        SubmitTicketCommand = new RelayCommand(_ => OpenSubmitTicket());
        RegenerateAiSummaryCommand = new RelayCommand(async _ => await RegenerateSelectedThreadStatusAsync());
        FontSettingsCommand = new RelayCommand(_ => OpenFontSettings());
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

    /// <summary>
    /// 对一组线索统一设置 产品/客户（rootEmailIds 为各线索的根邮件 Id，稳定不随线程重建变化），
    /// 并重建线程表使分组刷新。不能用 ThreadId 定位：后台自动同步重建线程后 ThreadId 会重新分配。
    /// </summary>
    public void SetMetaForThreads(IEnumerable<long> rootEmailIds, string product, string enterprise)
    {
        foreach (var id in rootEmailIds)
            _workflow.SetThreadMetaByRootEmail(id, product, enterprise);
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

    /// <summary>把一组线索（按根邮件 Id，稳定）原地移动到新产品/客户分组，不重建整棵树（避免窗口跳转/闪烁）。
    /// 数据库已由调用方更新（Emails + Threads 表），这里只增量调整 UI 树并保持选中。</summary>
    public void MoveThreadsToGroups(IReadOnlyList<long> rootEmailIds, string product, string enterprise)
    {
        if (rootEmailIds.Count == 0) return;
        // 记录当前选中（右侧详情），移动后恢复
        var selEmailId = SelectedEmail?.Email.Id ?? 0;
        var selRootId = SelectedThread?.Children.FirstOrDefault(c => c.IsRoot)?.Email.Id ?? 0;

        foreach (var id in rootEmailIds)
        {
            var node = FindRootNode(id);
            if (node == null) continue;
            var prod = FindProductOf(node);
            var cust = prod != null ? FindCustomerOf(prod) : null;
            if (prod == null) continue;

            // 1) 从旧分组移除（分组空则一并移除）
            prod.Threads.Remove(node);
            if (prod.Threads.Count == 0 && cust != null) cust.Products.Remove(prod);
            if (cust != null && cust.Products.Count == 0) Customers.Remove(cust);

            // 2) 更新内存中的线程数据（显示用；空值保持原样，与 DB 语义一致）
            var t = node.ThreadOwner.Thread;
            if (!string.IsNullOrEmpty(product)) t.Product = product;
            if (!string.IsNullOrEmpty(enterprise)) t.Enterprise = enterprise;
            node.RefreshThreadInfo();

            // 3) 定位/创建新分组并插入
            var custName = string.IsNullOrEmpty(t.Enterprise) ? "未分类客户" : t.Enterprise;
            var prodName = string.IsNullOrEmpty(t.Product) ? "未分类产品" : t.Product;
            var newCust = Customers.FirstOrDefault(c => c.Name == custName);
            if (newCust == null)
            {
                newCust = new CustomerGroupViewModel(custName) { ExpandedByDefault = ExpandDepth >= 2 };
                InsertCustomerSorted(newCust);
            }
            var newProd = newCust.Products.FirstOrDefault(p => p.Name == prodName);
            if (newProd == null)
            {
                newProd = new ProductGroupViewModel(prodName) { ExpandedByDefault = ExpandDepth >= 3 };
                InsertProductSorted(newCust, newProd);
            }
            InsertThreadSorted(newProd, node);
            // 4) 按 LastActivity 重排线索与分组顺序
            ReinsertThread(node);
        }

        // 恢复选中与多选高亮（节点已被移动，用稳定 Id 重新定位）
        if (selEmailId > 0 && FindEmailNodeById(selEmailId) is { } en)
        {
            SelectedEmail = en;
            SelectedThread = en.ThreadOwner;
        }
        else if (selRootId > 0 && FindRootNode(selRootId) is { } rn)
        {
            SelectedEmail = rn;
            SelectedThread = rn.ThreadOwner;
        }
        UpdateSelectionHighlight();
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

    /// <summary>停止正在进行的同步（手动 + 自动收取）。无进行中同步时不进入“正在停止…”状态。</summary>
    private void StopSync()
    {
        var manual = _manualSyncCts;
        var auto = _autoSyncCts;
        if (manual == null && auto == null)
        {
            StatusText = "当前没有进行中的同步";
            return;
        }
        manual?.Cancel();
        // 自动收取（2 分钟轮询）下载时也必须取消，否则“停止同步”停不掉正在下载的同步
        auto?.Cancel();
        StatusText = "正在停止同步…";
        App.Log("StopSync", new Exception($"已请求停止同步（manual={(manual != null)}, auto={(auto != null)}）"));
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
                        MergeThreads(_threads); // 增量合并新邮件到现有树，保留折叠层次
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
        _expandDepth = _workflow.GetExpandDepth(); // 恢复上次的“展开层次”设置
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
            // 后台自动同步：关键设置未完成则直接忽略（不弹窗询问、不启动自动收取）；
            // 只有手动点“同步”时才做配置缺失检查（EnsureSyncConfig，弹窗询问是否去设置）
            if (!HasSyncConfig()) return;
            await SyncAsync(ct);
            if (ct.IsCancellationRequested) return;
            StartAutoSync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>仅判断邮箱同步配置（Zoho REST 或 IMAP）是否齐全，不弹窗（供后台自动同步静默判断）。</summary>
    private bool HasSyncConfig()
    {
        var cfg = _workflow.Config;
        return (!string.IsNullOrEmpty(cfg.ZohoClientId) && !string.IsNullOrEmpty(cfg.ZohoRefreshToken)) ||
               (!string.IsNullOrEmpty(cfg.ImapHost) && !string.IsNullOrEmpty(cfg.ImapUsername));
    }

    /// <summary>同步前检查邮箱同步配置（Zoho REST 或 IMAP 任一配置即可）。
    /// 未配置则弹窗提示并打开设置窗口，返回 false；已配置返回 true 继续同步。
    /// DeepSeek 缺失不阻止同步（可选功能），仅在状态栏提示。</summary>
    private bool EnsureSyncConfig()
    {
        // 设置窗口已打开（用户正在配置）→ 不做配置检查，避免在配置过程中再弹“是否打开设置”提示
        if (Views.SettingsWindow.IsOpen) return false;
        var cfg = _workflow.Config;
        bool zoho = !string.IsNullOrEmpty(cfg.ZohoClientId) &&
                    !string.IsNullOrEmpty(cfg.ZohoRefreshToken);
        bool imap = !string.IsNullOrEmpty(cfg.ImapHost) &&
                    !string.IsNullOrEmpty(cfg.ImapUsername);
        if (zoho || imap)
        {
            if (string.IsNullOrEmpty(cfg.DeepSeekApiKey))
                StatusText = "提示：尚未配置 DeepSeek API，AI 总结/标题将不可用（可在设置中配置）";
            return true;
        }

        var missing = "· 邮箱同步（Zoho REST API 或 IMAP）";
        if (string.IsNullOrEmpty(cfg.DeepSeekApiKey))
            missing += "\n· DeepSeek API（AI 总结，可选）";
        var msg = "以下配置尚未完成，无法同步邮件：\n\n" + missing +
                  "\n\n是否现在打开设置窗口进行配置？";
        if (MessageBox.Show(msg, "需要配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            OpenSettings();
        return false;
    }

    /// <summary>重新加载线程并重建树（手工设置产品/客户后刷新）。</summary>
    public void Reload()
    {
        _threads = _workflow.LoadThreads();
        RebuildTree();
    }

    /// <summary>按根邮件 Id 手工设置线程状态：更新数据库 + 内存线程对象，并就地刷新根邮件显示（不重建树，避免闪烁）。</summary>
    public void SetThreadStatusByRootEmail(long rootEmailId, string status, string reason = "")
    {
        _workflow.SetThreadStatusByRootEmail(rootEmailId, status, reason);
        long updatedThreadId = 0;
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                {
                    if (root.Email.Id != rootEmailId) continue;
                    updatedThreadId = root.Email.ThreadId;
                    root.ThreadOwner.Thread.Status = status;
                    root.ThreadOwner.Thread.StatusSummary = "";
                    root.ThreadOwner.Thread.StatusReason = reason;
                    root.RefreshThreadInfo();
                    break;
                }
        // 若右侧详情面板显示的是该线程，同步刷新标题/总结/状态框配色/理由
        if (SelectedThread != null && SelectedThread.Thread.Id == updatedThreadId)
        {
            OnPropertyChanged(nameof(SelectedThreadHeader));
            OnPropertyChanged(nameof(SelectedThreadSummary));
            OnPropertyChanged(nameof(SelectedSummaryBorder));
            OnPropertyChanged(nameof(SelectedSummaryBackground));
            OnPropertyChanged(nameof(SelectedStatusReason));
            OnPropertyChanged(nameof(SelectedStatusReasonDisplay));
            OnPropertyChanged(nameof(HasStatusReason));
        }
    }

    /// <summary>用 AI 重新总结当前选中的线索（F9 热键）。未选中线索时不执行。</summary>
    private async Task RegenerateSelectedThreadStatusAsync()
    {
        var rootId = SelectedThread?.Children.FirstOrDefault(c => c.IsRoot)?.Email.Id ?? 0;
        if (rootId <= 0)
        {
            StatusText = "未选中工单线索，无法进行 AI 总结";
            return;
        }
        await RegenerateThreadStatusAsync(rootId);
    }

    /// <summary>立即用 AI 重新生成某线程的状态/总结并就地刷新显示（不重建树）。</summary>
    public async Task RegenerateThreadStatusAsync(long rootEmailId)
    {
        StatusText = "正在用 AI 总结该工单…";
        var r = await _workflow.RegenerateThreadStatusAsync(rootEmailId);
        if (r == null)
        {
            StatusText = "AI 总结失败（可能未配置 API Key 或调用出错）";
            return;
        }
        long updatedThreadId = 0;
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                {
                    if (root.Email.Id != rootEmailId) continue;
                    updatedThreadId = root.Email.ThreadId;
                    root.ThreadOwner.Thread.Status = r.Value.Status;
                    root.ThreadOwner.Thread.StatusSummary = r.Value.Summary;
                    root.ThreadOwner.Thread.StatusReason = ""; // AI 采纳，清空手工理由
                    root.RefreshThreadInfo();
                    break;
                }
        if (SelectedThread != null && SelectedThread.Thread.Id == updatedThreadId)
        {
            OnPropertyChanged(nameof(SelectedThreadHeader));
            OnPropertyChanged(nameof(SelectedThreadSummary));
            OnPropertyChanged(nameof(SelectedSummaryBorder));
            OnPropertyChanged(nameof(SelectedSummaryBackground));
            OnPropertyChanged(nameof(SelectedStatusReason));
            OnPropertyChanged(nameof(SelectedStatusReasonDisplay));
            OnPropertyChanged(nameof(HasStatusReason));
        }
        StatusText = "AI 总结已更新";
    }

    private async Task<bool> SyncAsync(CancellationToken externalCt = default)
    {
        if (IsBusy) return true;
        // 同步时检查邮箱同步配置：Zoho REST 或 IMAP 均未配置 → 提示并打开设置，不进入同步
        if (!EnsureSyncConfig()) return false;
        IsBusy = true;
        // 链接外部令牌（自动同步的取消）与手动取消源（停止同步），两者任一取消都会中断本次同步
        _manualSyncCts?.Dispose();
        _manualSyncCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _manualSyncCts.Token;
        App.Log("SyncAsync", new Exception("同步开始"));
        try
        {
            var progress = CreateProgress();
            var n = await _workflow.SyncAndProcessAsync(progress, ct);
            _threads = _workflow.LoadThreads();
            MergeThreads(_threads); // 增量合并新邮件到现有树，保留折叠层次
            StatusText = $"同步完成：新增 {n} 封邮件，共 {_threads.Count} 条线索";
            App.Log("SyncAsync", new Exception($"同步完成 新增 {n}"));
        }
        catch (OperationCanceledException)
        {
            // 同步被“停止同步”或自动同步取消：立即反馈停止，不在此处重建/合并
            // （取消时线程表未重建，LoadThreads/MergeThreads 拿不到新邮件且拖慢响应；
            //   已同步的邮件已保留在库中，下次同步或启动时会重建显示）
            App.Log("SyncAsync", new Exception("同步被取消（OperationCanceledException）"));
            StatusText = "同步已停止（已保留已同步的邮件）";
        }
        catch (Exception ex)
        {
            // 已入库的邮件仍在：增量合并刷新界面，避免“同步失败 = 数据全部丢失”的错觉
            App.Log("SyncAsync", ex);
            try
            {
                _threads = _workflow.LoadThreads();
                MergeThreads(_threads);
            }
            catch (Exception mergeEx) { App.Log("SyncAsync.MergeAfterFail", mergeEx); }
            StatusText = $"同步失败：{ex.Message}（已保留已同步的邮件）";
        }
        finally
        {
            App.Log("SyncAsync", new Exception("同步 finally 收尾"));
            IsBusy = false;
            ProgressIndeterminate = true;
            ProgressValue = 0;
            OnPropertyChanged(nameof(ProgressPercentText));
            _manualSyncCts?.Dispose();
            _manualSyncCts = null;
        }
        // 已尝试执行同步（无论成败）：配置齐全返回 true，供 AutoSyncAndListen 判断是否启动自动收取
        return true;
    }

    /// <summary>打开“提新工单”窗口（提交新故障给客服）。</summary>
    private void OpenSubmitTicket()
    {
        var win = new Views.SubmitTicketWindow(_workflow);
        win.ShowDialog();
    }

    /// <summary>打开“签名管理”窗口（维护多个邮件签名，供提新工单发信使用）。</summary>
    private void OpenSignatures()
    {
        var win = new Views.SignaturesWindow(_workflow);
        win.ShowDialog();
    }

    /// <summary>打开“邮件字体设置”窗口（统一应用于 邮件正文 与 签名）。</summary>
    private void OpenFontSettings()
    {
        var win = new Views.FontSettingsWindow(_workflow);
        if (Application.Current.MainWindow is { IsVisible: true } owner)
            win.Owner = owner;
        win.ShowDialog();
    }

    private void OpenSettings()
    {        var oldMonitored = _workflow.Config.MonitoredAddresses.ToList();
        var win = new Views.SettingsWindow(_workflow);
        // 仅当主窗口已显示才设为 Owner：主窗口未显示/隐藏到托盘时（如启动早期、窗口关闭到托盘），
        // 给未显示的 Window 设 Owner 会抛 InvalidOperationException，导致设置窗口打不开
        if (Application.Current.MainWindow is { IsVisible: true } owner)
            win.Owner = owner;
        // 仅当点击「保存」才刷新数据并按最新设置重启自动收取；「取消」不触发任何同步
        if (win.ShowDialog() == true)
        {
            Load();
            StartAutoSync();
            // 关注客服邮箱发生变化（新增/删除）→ 立即重新同步，拉取新关注邮箱相关的邮件
            var newMonitored = _workflow.Config.MonitoredAddresses;
            if (!SameAddressSet(oldMonitored, newMonitored))
                _ = SyncAsync();
        }
    }

    private static bool SameAddressSet(List<string> a, List<string> b)
    {
        var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return sa.SetEquals(sb);
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
        // 点击/选中新邮件 → 清除该邮件的新标记（查看即已读），并刷新整条线索的加粗状态
        if (ev.IsNew)
        {
            _workflow.MarkEmailSeen(ev.Email.Id);
            ev.Email.IsNew = false;
            foreach (var root in ev.ThreadOwner.Children)
                root.RefreshNewState();
        }
        SelectedEmail = ev;
        SelectedThread = ev.ThreadOwner;
    }

    /// <summary>把指定线索内的所有新同步邮件标记为已读（右键“全部已读”），并就地刷新加粗状态。</summary>
    public void MarkThreadSeen(long threadId)
    {
        // 用稳定的邮件 Id 批量清除（而非 ThreadId）：线程重建后 ThreadId 会重新分配，
        // 界面里的 ThreadId 可能与数据库不一致，按 ThreadId 更新会清 0 行 → 重启后仍显示未读
        EmailNodeViewModel? target = null;
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                    if (root.Email.ThreadId == threadId)
                    {
                        target = root;
                        break;
                    }
        if (target == null) return;
        var ids = new List<long>();
        CollectEmailIds(target, ids);
        if (ids.Count == 0) return;
        _workflow.MarkEmailsSeen(ids);
        ClearThreadNew(target); // 内存同步清除该线索下所有邮件的新标记
        target.RefreshNewState();
    }

    /// <summary>切换星标：根邮件（线索行）→ 整条线索所有邮件批量加/取消星标；其余邮件 → 只切本封。</summary>
    public void ToggleStar(EmailNodeViewModel node)
    {
        if (node.IsRoot)
        {
            // 线索星标：线索内任一邮件有星标 → 全部清除；否则全部标星
            var thread = node.ThreadOwner.Thread;
            var hasStar = thread.Emails.Any(e => e.Starred);
            foreach (var e in thread.Emails)
            {
                e.Starred = !hasStar;
                _workflow.SetEmailStarred(e.Id, !hasStar);
            }
            foreach (var root in node.ThreadOwner.Children)
                RefreshSubtreeStar(root);
            StatusText = hasStar ? $"已清除线索内 {thread.Emails.Count} 封邮件的星标" : $"已为该线索 {thread.Emails.Count} 封邮件标星";
        }
        else
        {
            node.Email.Starred = !node.Email.Starred;
            _workflow.SetEmailStarred(node.Email.Id, node.Email.Starred);
            node.RefreshStarState();
            // 单封星标变化会影响线索首节点（根）的“有星标”聚合显示，需一并刷新整条线索
            foreach (var root in node.ThreadOwner.Children)
                RefreshSubtreeStar(root);
        }
        // 不因星标变化立即重建“仅星标”过滤视图：取消星标后线索仍保留显示，直到切换开关/搜索/下次重建
    }

    /// <summary>递归刷新星标显示（批量切换线索星标后调用）。</summary>
    private static void RefreshSubtreeStar(EmailNodeViewModel n)
    {
        n.RefreshStarState();
        foreach (var c in n.Children) RefreshSubtreeStar(c);
    }

    /// <summary>把指定范围内的所有线索（客户/产品）的所有邮件标记为已读，并就地刷新加粗状态。</summary>
    public void MarkGroupSeen(IEnumerable<EmailNodeViewModel> roots)
    {
        var rootsList = roots.ToList();
        var ids = new List<long>();
        foreach (var root in rootsList) CollectEmailIds(root, ids);
        if (ids.Count == 0) return;
        _workflow.MarkEmailsSeen(ids);
        foreach (var root in rootsList)
        {
            ClearThreadNew(root); // 内存同步清除该范围内所有邮件的新标记
            root.RefreshNewState();
        }
    }

    private static void CollectEmailIds(EmailNodeViewModel n, List<long> ids)
    {
        ids.Add(n.Email.Id);
        foreach (var c in n.Children) CollectEmailIds(c, ids);
    }

    /// <summary>递归把某线索节点及其子树所有邮件的 IsNew 置为已读（配合“全部已读”内存刷新）。</summary>
    private static void ClearThreadNew(EmailNodeViewModel n)
    {
        n.Email.IsNew = false;
        foreach (var c in n.Children) ClearThreadNew(c);
    }

    private void RebuildTree()
    {
        // 保存当前选中（右侧详情面板），重建后按稳定 邮件 Id/根邮件 Id 恢复：
        // 设置产品/客户、重命名企业等重建树后 ThreadId 会变，但邮件 Id 稳定；
        // 不恢复的话，当前选中的线索被挪动后选中会丢失（SelectedEmail 残留树外旧对象）
        var selEmailId = SelectedEmail?.Email.Id ?? 0;
        var selRootId = SelectedThread?.Children.FirstOrDefault(c => c.IsRoot)?.Email.Id ?? 0;

        CaptureVmExpansion(); // 记录当前各节点的展开状态（含用户手动折叠），重建后保留
        BuildTree(_threads, _sortMode);
        if (_selectedThreadIds.Count > 0) UpdateSelectionHighlight(); // 重建后恢复多选高亮

        // 恢复选中：优先精确到选中的邮件，其次恢复选中线索的根邮件
        EmailNodeViewModel? restored = selEmailId > 0 ? FindEmailNodeById(selEmailId) : null;
        restored ??= selRootId > 0 ? FindRootNode(selRootId) : null;
        if (restored != null)
        {
            SelectedEmail = restored;
            SelectedThread = restored.ThreadOwner;
        }
        else if (selEmailId > 0 || selRootId > 0)
        {
            // 原选中已不存在（如被搜索过滤）：清空，避免右侧面板残留失效引用
            SelectedEmail = null;
            SelectedThread = null;
        }
    }

    // 重建树前捕获的展开状态（key：客户/产品名称、邮件 Id），用于自动同步等重建后保留用户的折叠层次
    private Dictionary<string, bool> _vmExpansion = new();

    private void CaptureVmExpansion()
    {
        var dict = new Dictionary<string, bool>();
        foreach (var cust in Customers)
        {
            dict["c:" + cust.Name] = cust.ExpandedByDefault;
            foreach (var prod in cust.Products)
            {
                dict["p:" + prod.Name] = prod.ExpandedByDefault;
                foreach (var root in prod.Threads)
                    CaptureEmailExpansion(root, dict);
            }
        }
        _vmExpansion = dict;
    }

    private static void CaptureEmailExpansion(EmailNodeViewModel node, Dictionary<string, bool> dict)
    {
        dict["e:" + node.Email.Id] = node.ExpandedByDefault;
        foreach (var c in node.Children) CaptureEmailExpansion(c, dict);
    }

    private void BuildTree(List<TicketThread> threads, TreeSortMode mode)
    {
        var list = new List<CustomerGroupViewModel>();
        var supportAddresses = _workflow.Config.MonitoredAddresses;
        var selfAddress = _workflow.Config.ImapUsername;
        var customerGroups = threads
            .Where(MatchesFilter)
            .GroupBy(t => string.IsNullOrEmpty(t.Enterprise) ? "未分类客户" : t.Enterprise);

        var orderedCustomers = mode switch
        {
            TreeSortMode.Time => customerGroups.OrderByDescending(g => g.Max(t => t.LastActivity)),
            _ => customerGroups.OrderBy(g => g.Key, StringComparer.Ordinal)
        };

        foreach (var cg in orderedCustomers)
        {
            var customer = new CustomerGroupViewModel(cg.Key)
            {
                ExpandedByDefault = _vmExpansion.TryGetValue("c:" + cg.Key, out var ce) ? ce : ExpandDepth >= 2
            };
            var productGroups = cg
                .GroupBy(t => string.IsNullOrEmpty(t.Product) ? "未分类产品" : t.Product);

            var orderedProducts = mode switch
            {
                TreeSortMode.Time => productGroups.OrderByDescending(g => g.Max(t => t.LastActivity)),
                _ => productGroups.OrderBy(g => g.Key, StringComparer.Ordinal)
            };

            bool customerAllClosed = true; // 该企业下是否所有产品都已结束（结束→默认折叠企业）
            foreach (var pg in orderedProducts)
            {
                var product = new ProductGroupViewModel(pg.Key)
                {
                    ExpandedByDefault = _vmExpansion.TryGetValue("p:" + pg.Key, out var pe) ? pe : ExpandDepth >= 3
                };
                // 同一产品下的所有邮件树一律按最后更新时间倒序（与全局排序模式无关）
                var orderedThreads = pg.OrderByDescending(t => t.LastActivity).ToList();
                bool productAllClosed = orderedThreads.Count > 0; // 该产品下是否所有线索都已结束（结束→默认折叠产品）
                foreach (var t in orderedThreads)
                {
                    // 工单层并入根邮件节点：状态/工单号/总结显示在根邮件上，不再有独立工单行
                    var owner = new ThreadViewModel(t, supportAddresses, selfAddress);
                    foreach (var root in owner.Children)
                    {
                        SetEmailExpandDepth(root, ExpandDepth);
                        // 线索已结束（已完成/已关闭/已合并）：默认折叠该线索（尊重用户手动展开）
                        if (IsThreadClosed(t) && !_vmExpansion.ContainsKey("e:" + root.Email.Id))
                            root.ExpandedByDefault = false;
                        product.Threads.Add(root);
                    }
                    if (!IsThreadClosed(t)) productAllClosed = false;
                }
                // 产品下所有线索都已结束 → 产品默认折叠（尊重用户手动展开）
                if (productAllClosed && !_vmExpansion.ContainsKey("p:" + pg.Key))
                    product.ExpandedByDefault = false;
                customer.Products.Add(product);
                if (!productAllClosed) customerAllClosed = false;
            }
            // 企业下所有产品都已结束 → 企业默认折叠（尊重用户手动展开）
            if (customerAllClosed && !_vmExpansion.ContainsKey("c:" + cg.Key))
                customer.ExpandedByDefault = false;
            list.Add(customer);
        }
        ReplaceCustomers(list);
    }

    /// <summary>视为“已结束、无需继续关注”的线索状态：已完成/已解决、已关闭、合并或拆分为其他工单。</summary>
    private static readonly HashSet<string> ClosedThreadStatuses = new(StringComparer.Ordinal)
    {
        "已解决", "已完成", "已关闭", "合并或拆分为其他工单"
    };

    /// <summary>线索是否已结束（已完成/已解决、已关闭、合并或拆分为其他工单），供“智能折叠”使用。</summary>
    public static bool IsThreadClosed(TicketThread t)
        => ClosedThreadStatuses.Contains(t.Status);

    /// <summary>递归设置邮件节点默认展开：优先保留重建前用户手动折叠状态，否则按展开层次（仅层次4展开）。</summary>
    private void SetEmailExpandDepth(EmailNodeViewModel node, int depth)
    {
        node.ExpandedByDefault = _vmExpansion.TryGetValue("e:" + node.Email.Id, out var e) ? e : depth >= 4;
        foreach (var c in node.Children) SetEmailExpandDepth(c, depth);
    }

    // ================= 同步后增量更新（不重建整棵树，保留折叠层次）=================

    /// <summary>
    /// 同步后把新线程增量合并进现有树：新增线索插入正确分组、已有线索仅在其邮件数变化时重建该线索子树（保留展开状态）、
    /// 移除已消失的线索。不重建整棵树，因此用户手动折叠/展开的状态得以保留。
    /// </summary>
    public void MergeThreads(List<TicketThread> newThreads)
    {
        var support = _workflow.Config.MonitoredAddresses;
        var self = _workflow.Config.ImapUsername;

        // 合并/排序可能替换或移动选中节点：用稳定标识（邮件 Id / 线索根邮件 Id）保存选中，合并后恢复
        var selEmailId = SelectedEmail?.Email.Id ?? 0;
        var selThreadRootId = SelectedThread?.Children.FirstOrDefault(c => c.IsRoot)?.Email.Id ?? 0;

        var newRootIds = new HashSet<long>();
        foreach (var t in newThreads)
            if (GetRootEmail(t) is { } r) newRootIds.Add(r.Id);

        // 1. 移除已消失或不再匹配过滤的线索（并清理空分组）；
        //    “仅新邮件/仅星标/检索关键字”模式下同时隐藏已不满足条件的线索
        foreach (var cust in Customers.ToList())
            foreach (var prod in cust.Products.ToList())
                foreach (var root in prod.Threads.ToList())
                    if (!newRootIds.Contains(root.Email.Id) ||
                        !MatchesFilter(root.ThreadOwner.Thread))
                        prod.Threads.Remove(root);
        foreach (var cust in Customers.ToList())
            foreach (var prod in cust.Products.ToList())
                if (prod.Threads.Count == 0) cust.Products.Remove(prod);
        foreach (var cust in Customers.ToList())
            if (cust.Products.Count == 0) Customers.Remove(cust);

        // 2. 插入新线索 / 更新已有线索（只处理匹配当前全部过滤的线索）
        foreach (var t in newThreads)
        {
            if (!MatchesFilter(t)) continue;
            var rootEmail = GetRootEmail(t);
            if (rootEmail == null) continue;
            var existing = FindRootNode(rootEmail.Id);
            if (existing == null)
                InsertNewThread(t, support, self);
            else if (existing.ThreadOwner.Thread.EmailCount != t.EmailCount)
                UpdateExistingThread(existing, t, support, self);
        }

        // 3. 恢复选中（节点可能被替换/移动，用稳定标识重新定位）
        RestoreSelectionAfterMerge(selEmailId, selThreadRootId);
        SelectionRestoredAfterMerge?.Invoke(); // 通知主窗口在 TreeView 中同步高亮选中
    }

    /// <summary>合并后按稳定标识恢复选中：优先选中的邮件，其次选中的线索（根邮件）。</summary>
    private void RestoreSelectionAfterMerge(long emailId, long threadRootId)
    {
        if (emailId > 0 && FindEmailNodeById(emailId) is { } node)
        {
            SelectedEmail = node;
            SelectedThread = node.ThreadOwner;
            return;
        }
        if (threadRootId > 0 && FindRootNode(threadRootId) is { } root)
        {
            SelectedEmail = root;
            SelectedThread = root.ThreadOwner;
        }
    }

    /// <summary>在整棵树中按邮件 Id 查找节点（含非根邮件）。</summary>
    private EmailNodeViewModel? FindEmailNodeById(long emailId)
    {
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                    if (FindInSubtree(root, emailId) is { } n)
                        return n;
        return null;
    }

    private static EmailNodeViewModel? FindInSubtree(EmailNodeViewModel node, long emailId)
    {
        if (node.Email.Id == emailId) return node;
        foreach (var c in node.Children)
            if (FindInSubtree(c, emailId) is { } n) return n;
        return null;
    }

    /// <summary>线索的首邮件（根邮件）。</summary>
    private static EmailMessage? GetRootEmail(TicketThread t)
    {
        if (t.DisplayRoots.Count > 0) return t.DisplayRoots[0].Email;
        return t.Emails.OrderBy(e => e.DateSent).FirstOrDefault();
    }

    /// <summary>在现有树中按根邮件 Id 查找线索根节点。</summary>
    private EmailNodeViewModel? FindRootNode(long rootEmailId)
    {
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                    if (root.IsRoot && root.Email.Id == rootEmailId)
                        return root;
        return null;
    }

    /// <summary>把新线索插入正确分组（客户/产品分组不存在则创建，保持排序）。</summary>
    private void InsertNewThread(TicketThread t, IReadOnlyList<string> support, string self)
    {
        var enterprise = string.IsNullOrEmpty(t.Enterprise) ? "未分类客户" : t.Enterprise;
        var productName = string.IsNullOrEmpty(t.Product) ? "未分类产品" : t.Product;

        var cust = Customers.FirstOrDefault(c => c.Name == enterprise);
        if (cust == null)
        {
            cust = new CustomerGroupViewModel(enterprise) { ExpandedByDefault = ExpandDepth >= 2 };
            InsertCustomerSorted(cust);
        }
        var prod = cust.Products.FirstOrDefault(p => p.Name == productName);
        if (prod == null)
        {
            prod = new ProductGroupViewModel(productName) { ExpandedByDefault = ExpandDepth >= 3 };
            InsertProductSorted(cust, prod);
        }
        var owner = new ThreadViewModel(t, support, self);
        foreach (var root in owner.Children)
        {
            SetEmailExpandDepth(root, ExpandDepth);
            // 增量同步新增的已结束线索默认折叠（尊重用户手动展开）
            if (IsThreadClosed(t) && !_vmExpansion.ContainsKey("e:" + root.Email.Id))
                root.ExpandedByDefault = false;
            InsertThreadSorted(prod, root);
        }
        // 新线索的 LastActivity 可能改变所属 产品/客户 分组的 Max(LastActivity) 排序
        if (owner.Children.Count > 0) ReinsertThread(owner.Children[0]);
    }

    /// <summary>已有线索邮件数变化：重建该线索子树并迁移展开状态（保留折叠层次）。</summary>
    private void UpdateExistingThread(EmailNodeViewModel existingRoot, TicketThread t, IReadOnlyList<string> support, string self)
    {
        var expansion = new Dictionary<long, bool>();
        CaptureEmailExpansionById(existingRoot, expansion);

        var prod = FindProductOf(existingRoot);
        if (prod == null) return;
        var idx = prod.Threads.IndexOf(existingRoot);
        if (idx < 0) return;

        var owner = new ThreadViewModel(t, support, self);
        foreach (var newRoot in owner.Children)
            MigrateEmailExpansion(newRoot, expansion);

        foreach (var newRoot in owner.Children)
            prod.Threads.Insert(idx, newRoot);
        prod.Threads.RemoveAt(idx + owner.Children.Count);

        // 新邮件更新了 LastActivity：按时间重新排线索，并重排所属 产品/客户 分组（Max(LastActivity) 可能变化）
        var firstRoot = owner.Children.FirstOrDefault();
        if (firstRoot != null) ReinsertThread(firstRoot);
    }

    /// <summary>按 LastActivity 重新定位线索，并对其所属 产品/客户 分组按 Max(LastActivity) 重新排序。</summary>
    private void ReinsertThread(EmailNodeViewModel root)
    {
        var prod = FindProductOf(root);
        if (prod == null) return;
        var cust = FindCustomerOf(prod);
        // 线索在产品内按 LastActivity 倒序重排
        prod.Threads.Remove(root);
        InsertThreadSorted(prod, root);
        // 产品在客户下按 Max(LastActivity) 重排
        if (cust != null)
        {
            cust.Products.Remove(prod);
            InsertProductSorted(cust, prod);
            // 客户在顶层按 Max(LastActivity) 重排
            Customers.Remove(cust);
            InsertCustomerSorted(cust);
        }
    }

    private CustomerGroupViewModel? FindCustomerOf(ProductGroupViewModel prod)
    {
        foreach (var cust in Customers)
            if (cust.Products.Contains(prod))
                return cust;
        return null;
    }

    private static void CaptureEmailExpansionById(EmailNodeViewModel node, Dictionary<long, bool> dict)
    {
        dict[node.Email.Id] = node.ExpandedByDefault;
        foreach (var c in node.Children) CaptureEmailExpansionById(c, dict);
    }

    private static void MigrateEmailExpansion(EmailNodeViewModel node, Dictionary<long, bool> expansion)
    {
        if (expansion.TryGetValue(node.Email.Id, out var e)) node.ExpandedByDefault = e;
        foreach (var c in node.Children) MigrateEmailExpansion(c, expansion);
    }

    private ProductGroupViewModel? FindProductOf(EmailNodeViewModel root)
    {
        foreach (var cust in Customers)
            foreach (var prod in cust.Products)
                if (prod.Threads.Contains(root))
                    return prod;
        return null;
    }

    private void InsertThreadSorted(ProductGroupViewModel prod, EmailNodeViewModel root)
    {
        var last = root.ThreadOwner.Thread.LastActivity;
        int i = 0;
        while (i < prod.Threads.Count && prod.Threads[i].ThreadOwner.Thread.LastActivity > last) i++;
        prod.Threads.Insert(i, root);
    }

    private void InsertCustomerSorted(CustomerGroupViewModel cust)
    {
        int i = 0;
        if (_sortMode == TreeSortMode.Time)
        {
            var myMax = CustomerMaxActivity(cust);
            while (i < Customers.Count && CustomerMaxActivity(Customers[i]) > myMax) i++;
        }
        else
        {
            while (i < Customers.Count && string.Compare(Customers[i].Name, cust.Name, StringComparison.Ordinal) < 0) i++;
        }
        Customers.Insert(i, cust);
    }

    private void InsertProductSorted(CustomerGroupViewModel cust, ProductGroupViewModel prod)
    {
        int i = 0;
        if (_sortMode == TreeSortMode.Time)
        {
            var myMax = ProductMaxActivity(prod);
            while (i < cust.Products.Count && ProductMaxActivity(cust.Products[i]) > myMax) i++;
        }
        else
        {
            while (i < cust.Products.Count && string.Compare(cust.Products[i].Name, prod.Name, StringComparison.Ordinal) < 0) i++;
        }
        cust.Products.Insert(i, prod);
    }

    /// <summary>客户组最大活动时间：无产品/无线程时返回 MinValue（避免空集合 Max 抛异常）。</summary>
    private static DateTimeOffset CustomerMaxActivity(CustomerGroupViewModel c)
    {
        var max = DateTimeOffset.MinValue;
        foreach (var p in c.Products)
            foreach (var t in p.Threads)
                if (t.ThreadOwner.Thread.LastActivity > max) max = t.ThreadOwner.Thread.LastActivity;
        return max;
    }

    /// <summary>产品组最大活动时间：无线程时返回 MinValue（避免空集合 Max 抛异常）。</summary>
    private static DateTimeOffset ProductMaxActivity(ProductGroupViewModel p)
    {
        var max = DateTimeOffset.MinValue;
        foreach (var t in p.Threads)
            if (t.ThreadOwner.Thread.LastActivity > max) max = t.ThreadOwner.Thread.LastActivity;
        return max;
    }
}
