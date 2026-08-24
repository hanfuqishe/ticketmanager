using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClosedXML.Excel;
using Microsoft.Win32;
using TicketManager.Models;
using TicketManager.ViewModels;

namespace TicketManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(App.Workflow);
        DataContext = _vm;
        _vm.Load();
        SetupTray();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.EmailBodyDisplay) or nameof(MainViewModel.SelectedEmailBody))
                RenderEmailBody();
        };
        _vm.JumpToNewMailRequested += JumpNewMail;
        _vm.SelectionRestoredAfterMerge += RestoreSelectionInTree;
        RenderEmailBody(); // 初始（空）正文
        _vm.AutoSyncAndListen(); // 启动后自动同步，随后进入自动收取新邮件模式
    }

    /// <summary>把正文渲染进 RichTextBox：当前正文黑色，引用默认折叠为可展开链接，展开后灰色斜体显示。</summary>
    private void RenderEmailBody()
    {
        if (EmailBodyBox == null) return;
        _pendingQuote = null; // 重置待展开引用（切换邮件/显示切换时）
        var text = _vm.EmailBodyDisplay;
        if (string.IsNullOrWhiteSpace(text))
        {
            EmailBodyBox.Document = new FlowDocument();
            return;
        }

        int idx = FindQuoteStart(text);
        var normal = idx > 0 ? text[..idx] : text;
        var quote = idx > 0 ? text[idx..] : "";

        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        var bodyPara = new Paragraph(new Run(normal)) { Foreground = Brushes.Black, LineHeight = 1.25 };
        doc.Blocks.Add(bodyPara);

        if (!string.IsNullOrEmpty(quote))
        {
            // 引用默认折叠：正文上方显示“展开引用”按钮，点击后插入完整引用（含嵌套引用）
            _pendingQuote = quote;
            ExpandQuoteButton.Visibility = Visibility.Visible;
        }
        else
        {
            ExpandQuoteButton.Visibility = Visibility.Collapsed;
        }
        EmailBodyBox.Document = doc;
    }

    /// <summary>点击“展开引用”按钮：把完整引用（含嵌套）以灰色斜体插入正文之后，并隐藏按钮。</summary>
    private void ExpandQuote_Click(object sender, RoutedEventArgs e)
    {
        var quote = _pendingQuote;
        _pendingQuote = null;
        ExpandQuoteButton.Visibility = Visibility.Collapsed;
        if (string.IsNullOrEmpty(quote)) return;
        if (EmailBodyBox.Document is not FlowDocument doc) return;
        var quotePara = new Paragraph(new Run(quote))
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
            FontStyle = FontStyles.Italic
        };
        doc.Blocks.Add(quotePara);
    }

    /// <summary>定位邮件正文中“引用部分”的起始位置；没有引用返回 -1。</summary>
    private static int FindQuoteStart(string body)
    {
        int best = -1;
        string[] markers =
        {
            "-----Original Message-----", "----- 原始邮件 -----", "-----原始邮件-----",
            "---- 原始邮件 ----", "--- 原始邮件 ---", "---- 原始消息 ----",
            "----- 原始邮件内容 -----", "----- 原始邮件附件 -----"
        };
        foreach (var mk in markers)
        {
            int i = body.IndexOf(mk, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }

        // 各类引用起始模式（含英文 On...wrote、日期+wrote、Outlook From/Sent、中文 在...写道、分隔线等）
        string[] patterns =
        {
            @"(?m)^\s*(?:>\s*)?On\s+.+?wrote",                          // 英文：On Wed, Aug 19, 2026 ... wrote:
            @"(?m)^\s*(?:>\s*)?.*?\b(?:19|20)\d{2}\b.*?\bwrote\b",      // 英文：Aug 2026 07:06:07 +0530 "x"<y> wrote ----
            @"(?m)^\s*(?:>\s*)?Sent:\s+",                               // Outlook 引用头
            @"(?m)^\s*(?:>\s*)?From:\s+.+?\n\s*Sent:",                  // Outlook From: + Sent:
            @"(?m)^\s*在\s+.+?写道",                                    // 中文：在 ... 写道
            @"(?m)^-{3,}\s*$",                                          // ---- 分隔线
            @"(?m)^\s*[-—]{3,}\s*[-—]?\s*[一二三四五六日]?,?\s*\d{1,2}\s*\S+\s*\d{4}.*?(?:写|wrote)", // 中文客户端
            @"(?m)^\s*>[-—]{3,}",                                       // > ---- 分隔
            @"(?m)^\s*发件人\s*:",                                      // 中文客户端：发件人: xxx
            @"(?m)^\s*(?:收件人|抄送|日期|主题)\s*:",                    // 中文客户端：收件人/抄送/日期/主题:
            @"(?m)^\s*[-—–][\s\-—–]*$",                                 // 分隔线（含空格的连字符/破折号，如 - - - -）
            @"(?m)^\s*[-—]{3,}\s*(?:Replied|Original|Forwarded|Transferred|回复|转发|原始)\s*(?:Message|邮件)?\s*[-—]{3,}" // ---- Replied Message ---- 等
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(body, p);
            if (m.Success && (best < 0 || m.Index < best)) best = m.Index;
        }
        return best;
    }

    /// <summary>当前邮件未展开的引用文本（展开后置空）。</summary>
    private string? _pendingQuote;

    /// <summary>初始化系统托盘图标（最小化时隐藏到托盘）。</summary>
    private void SetupTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon();
        try
        {
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/TicketManager.ico"))?.Stream;
            if (s != null) _trayIcon.Icon = new System.Drawing.Icon(s);
        }
        catch { }
        _trayIcon.Text = "工单邮件管理器";
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.Visible = false;
    }

    /// <summary>最小化时隐藏到系统托盘（后台仍监听新邮件）。</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _trayIcon != null)
        {
            Hide();
            _trayIcon.Visible = true;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(1500, "工单邮件管理器",
                    "程序已最小化到系统托盘，仍在后台监听新邮件。", System.Windows.Forms.ToolTipIcon.Info);
            }
        }
    }

    private void RestoreFromTray()
    {
        if (_trayIcon != null) _trayIcon.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>挂窗口消息钩子，响应第二个实例的“显示主窗口”请求（窗口在托盘/最小化时也能自行恢复）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == App.ShowMainMessage)
        {
            RestoreFromTray();
            Topmost = true;
            Topmost = false; // 强制置顶一次，确保抢到前台焦点
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _vm.StopAutoSync(); // 关闭时停止后台自动收取
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosing(e);
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // 记录当前选中的范围（客户/产品），主菜单“导出”据此动态决定导出范围并更新菜单文本
        _exportScopeTag = e.NewValue switch
        {
            CustomerGroupViewModel cg => cg,
            ProductGroupViewModel pg => pg,
            _ => null
        };
        UpdateExportMenuText();

        // 原有跳转：点击线索/邮件 → 右侧详情
        switch (e.NewValue)
        {
            case ThreadViewModel tv:
                _vm.SelectThread(tv);
                break;
            case EmailNodeViewModel ev:
                _vm.SelectEmail(ev);
                break;
        }

        // Ctrl/Shift 多选：仅在原生选中变化时维护多选集合（不干预 TreeView 原生选中）
        if (e.NewValue is EmailNodeViewModel ev2 && ev2.IsRoot)
        {
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control))
            {
                var ids = _vm.SelectedThreadIds.ToHashSet();
                if (!ids.Remove(ev2.Email.ThreadId)) ids.Add(ev2.Email.ThreadId);
                _selectionAnchorThreadId = ev2.Email.ThreadId;
                _vm.ApplySelection(ids);
            }
            else if (mods.HasFlag(ModifierKeys.Shift))
            {
                var ids = _vm.SelectedThreadIds.ToHashSet();
                if (_selectionAnchorThreadId is long anchor && anchor != ev2.Email.ThreadId)
                {
                    var siblings = SiblingRoots(ev2.Email.ThreadId);
                    int a = siblings.FindIndex(x => x.Email.ThreadId == anchor);
                    int b = siblings.FindIndex(x => x.Email.ThreadId == ev2.Email.ThreadId);
                    if (a >= 0 && b >= 0)
                        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                            ids.Add(siblings[i].Email.ThreadId);
                    else
                        ids.Add(ev2.Email.ThreadId);
                }
                else
                {
                    ids.Add(ev2.Email.ThreadId);
                }
                _selectionAnchorThreadId = ev2.Email.ThreadId;
                _vm.ApplySelection(ids);
            }
            else
            {
                // 普通点击：清空多选集合与高亮（原生选中已由 TreeView 处理，右侧正常跳转）
                _selectionAnchorThreadId = ev2.Email.ThreadId;
                _vm.ClearSelection();
            }
        }
    }

    private long? _selectionAnchorThreadId; // Shift 范围选择的锚点

    /// <summary>返回包含指定线程 Id 的产品分组内的全部线索根节点（用于 Shift 范围选择）。</summary>
    private List<EmailNodeViewModel> SiblingRoots(long threadId)
    {
        foreach (var cust in _vm.Customers)
            foreach (var prod in cust.Products)
                if (prod.Threads.Any(t => t.Email.ThreadId == threadId))
                    return prod.Threads.ToList();
        return new List<EmailNodeViewModel>();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = "";
    }

    /// <summary>展开层次（视图 → 展开层次）：一次性动作，只展开到目标层次，不持久化、不勾选；之后由用户操作决定展开状态。</summary>
    private async void SetExpandDepth_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || !int.TryParse(mi.Tag as string, out var depth)) return;
        Mouse.OverrideCursor = Cursors.Wait; // 切换时显示等待光标
        try
        {
            await Task.Yield(); // 先让等待光标渲染出来
            // 一次性动作：只显式逐层应用展开状态，不写 _vm.ExpandDepth（不持久化为“当前层次”）
            // 等容器就绪后显式逐层应用展开状态，应用完成后再恢复光标
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                TreeView.UpdateLayout();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    ApplyExpandDepth(depth);
                    Mouse.OverrideCursor = null;
                    _vm.StatusText = $"展开层次已设为：{depth switch { 1 => "只显示用户名称", 2 => "显示到产品名称", 3 => "显示到线索首邮件", _ => "显示所有邮件" }}";
                }));
            }));
        }
        catch
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>显式按展开层次设置各层节点的 IsExpanded（不依赖容器绑定的重新求值）。</summary>
    private void ApplyExpandDepth(int depth)
    {
        void Walk(ItemsControl parent)
        {
            foreach (var item in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
                tvi.IsExpanded = item switch
                {
                    CustomerGroupViewModel => depth >= 2,
                    ProductGroupViewModel => depth >= 3,
                    EmailNodeViewModel => depth >= 4,
                    _ => tvi.IsExpanded
                };
                Walk(tvi);
            }
        }
        Walk(TreeView);
    }

    /// <summary>右键菜单打开时，动态填充「设置产品/设置客户」子菜单的候选列表。</summary>
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not EmailNodeViewModel ev) return;
        // 目标集合：右键线索在 Ctrl/Shift 选中集内 → 应用到整个选中集；否则仅当前线索
        var targets = _vm.SelectedThreadIds.Contains(ev.Email.ThreadId)
            ? _vm.SelectedThreadIds.ToList()
            : new List<long> { ev.Email.ThreadId };
        var setProduct = FindMenuItem(menu, "设置产品");
        var setEnterprise = FindMenuItem(menu, "设置客户");
        if (setProduct != null) PopulateMetaMenu(setProduct, targets, ev, isProduct: true);
        if (setEnterprise != null) PopulateMetaMenu(setEnterprise, targets, ev, isProduct: false);
    }

    private static MenuItem? FindMenuItem(ItemsControl parent, string header) =>
        parent.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == header);

    private void PopulateMetaMenu(MenuItem sub, List<long> targets, EmailNodeViewModel ev, bool isProduct)
    {
        sub.Items.Clear();
        var names = isProduct ? App.Workflow.GetKnownProducts() : App.Workflow.GetKnownEnterprises();
        var current = isProduct ? ev.Email.Product : ev.Email.Enterprise;
        var multi = targets.Count > 1;
        if (multi)
            sub.Header = (isProduct ? "设置产品" : "设置客户") + $"（{targets.Count} 条）";
        foreach (var name in names)
        {
            var mi = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(name, current, StringComparison.Ordinal),
                Tag = (targets, name, isProduct)
            };
            mi.Click += SetMetaDirect_Click;
            sub.Items.Add(mi);
        }
        sub.IsEnabled = names.Count > 0;
    }

    private async void SetMetaDirect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem mi || mi.Tag is not (List<long> targets, string name, bool isProduct)) return;
            // 先记录被设置线索的根邮件 Id（线程重建后 ThreadId 会变，邮件 Id 稳定），用于保持选中
            var rootEmailIds = _vm.GetRootEmailIds(targets);
            MetaLog($"开始: targets=[{string.Join(",", targets)}] name={name} isProduct={isProduct} rootEmailIds=[{string.Join(",", rootEmailIds)}]");
            var product = isProduct ? name : "";
            var enterprise = isProduct ? "" : name;
            // 后台：更新数据库（Emails + Threads 表，只动目标线程，不重建整个线程表）
            await Task.Run(() =>
            {
                foreach (var id in rootEmailIds)
                    App.Workflow.SetThreadMetaByRootEmail(id, product, enterprise);
            });
            // UI 线程：在现有树中增量移动线索节点，不重建整棵树（避免窗口跳转/闪烁），并保持选中
            _vm.MoveThreadsToGroups(rootEmailIds, product, enterprise);
            // 若被移动的线索就是“当前选中”线索：保持选中不变，并把 TreeView 高亮拉回、滚动到它的新显示位置
            // （从旧分组 Remove 时 TreeView 会自动把选中跳到下一个节点，需要重新定位选中）
            var curRootId = _vm.SelectedThread?.Children.FirstOrDefault(c => c.IsRoot)?.Email.Id ?? 0;
            if (curRootId > 0 && rootEmailIds.Contains(curRootId) && _vm.SelectedEmail is { } sel)
                SelectAndScrollToEmail(sel);
            MetaLog($"移动后 目标分组=" + (rootEmailIds.Count > 0 ? FindGroupOfRoot(rootEmailIds[0]) : "无"));
            _vm.StatusText = $"已为 {targets.Count} 条线索设置 {(isProduct ? "产品：" + name : "客户：" + name)}";
        }
        catch (Exception ex)
        {
            App.Log("SetMetaDirect", ex);
            _vm.StatusText = "设置失败：" + ex.Message;
        }
    }

    /// <summary>返回指定根邮件所在客户分组名（诊断用；找不到返回 ?）。</summary>
    private string FindGroupOfRoot(long rootEmailId)
    {
        foreach (var cust in _vm.Customers)
            foreach (var prod in cust.Products)
                foreach (var r in prod.Threads)
                    if (r.IsRoot && r.Email.Id == rootEmailId)
                        return cust.Name + "/" + prod.Name;
        return "?";
    }

    /// <summary>写设置诊断日志到 %AppData%\TicketManager\metaset.log。</summary>
    private void MetaLog(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TicketManager", "metaset.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    /// <summary>设置产品/客户后：定位指定根邮件所在线索并滚动选中（复用跳转新邮件的容器就绪机制）。</summary>
    private void SelectRootEmailById(long rootEmailId)
    {
        if (rootEmailId <= 0) return;
        EmailNodeViewModel? target = null;
        foreach (var cust in _vm.Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                    if (root.IsRoot && root.Email.Id == rootEmailId)
                    {
                        target = root;
                        break;
                    }
        if (target == null) return;
        SelectAndScrollToEmail(target);
    }



    private void ClearMeta_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not EmailNodeViewModel ev) return;
        App.Workflow.SetEmailMeta(ev.Email.Id, "", "");
        ReloadPreservingExpansion();
    }

    private void SetStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        var status = (string)mi.Header;
        // 手工设置状态：弹出输入框填写理由（AI 归纳意见作为默认内容，可修改；下次选中该线索时显示）
        var win = new TicketManager.Views.StatusReasonWindow(status, ev.ThreadOwner?.Thread.StatusReason ?? "", ev.ThreadOwner?.Thread.StatusSummary ?? "")
        {
            Owner = this
        };
        if (win.ShowDialog() == true)
            _vm.SetThreadStatusByRootEmail(ev.Email.Id, status, win.Reason);
    }

    /// <summary>导出右键范围（客户/产品）内的所有线索为 CSV：工单号/名称/客户/产品/开始时间/最后更新/AI状态/AI详情。</summary>
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var threads = CollectScopeThreads(mi.Tag, out var scopeName);
        if (threads == null) return;
        ExportCsvCore(threads, scopeName);
    }

    /// <summary>主菜单：按当前选中范围（客户/产品）导出线索为 CSV，无选中则全部。</summary>
    private void ExportAllCsv_Click(object sender, RoutedEventArgs e)
        => ExportCsvCore(ResolveExportScope(out var scopeName), scopeName);

    /// <summary>CSV 导出主体：对话框 + 写文件 + 完成提示。</summary>
    private void ExportCsvCore(List<TicketThread> threads, string scopeName)
    {
        if (threads.Count == 0)
        {
            MessageBox.Show(this, "该范围内没有线索可导出。", "导出 CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出线索为 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"线索导出_{SanitizeFileName(scopeName)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using (var sw = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("工单号,线索名称,客户,产品,开始时间,最后更新时间,AI状态,AI详情");
                foreach (var t in threads)
                {
                    var row = new[]
                    {
                        t.TicketNumber, RootSubject(t), t.Enterprise,
                        string.IsNullOrWhiteSpace(t.Product) ? "未分类产品" : t.Product,
                        t.FirstActivity.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        t.LastActivity.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        t.Status, t.StatusSummary
                    };
                    sw.WriteLine(string.Join(",", row.Select(CsvEscape)));
                }
            }
            // 文件流已释放（using 块结束）再弹窗，避免 Excel 打开时提示“文件被锁定”
            ShowExportComplete(dlg.FileName, threads.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出 CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>收集右键范围（客户/产品）内的所有线索；scopeName 为文件名范围名（产品时含上级企业名）。</summary>
    private List<TicketThread>? CollectScopeThreads(object? tag, out string scopeName)
    {
        scopeName = "";
        var threads = new List<TicketThread>();
        switch (tag)
        {
            case CustomerGroupViewModel cust:
                scopeName = cust.Name;
                foreach (var p in cust.Products)
                    foreach (var root in p.Threads)
                        threads.Add(root.ThreadOwner.Thread);
                break;
            case ProductGroupViewModel prod:
                scopeName = prod.Name;
                foreach (var root in prod.Threads)
                    threads.Add(root.ThreadOwner.Thread);
                // 产品名前面带上所属上级企业名，便于区分不同企业下的同名产品
                foreach (var cust in _vm.Customers)
                    if (cust.Products.Contains(prod))
                    {
                        scopeName = $"{cust.Name}_{prod.Name}";
                        break;
                    }
                break;
            default:
                return null;
        }
        return threads.Distinct().OrderByDescending(t => t.LastActivity).ToList();
    }

    /// <summary>收集当前树中全部线索（主菜单“导出全部”用）。</summary>
    private List<TicketThread> CollectAllThreads(out string scopeName)
    {
        scopeName = "全部";
        var threads = new List<TicketThread>();
        foreach (var cust in _vm.Customers)
            foreach (var p in cust.Products)
                foreach (var root in p.Threads)
                    threads.Add(root.ThreadOwner.Thread);
        return threads.Distinct().OrderByDescending(t => t.LastActivity).ToList();
    }

    /// <summary>当前选中的客户/产品节点（主菜单导出范围用）；选中其他节点则为 null（导出全部）。</summary>
    private object? _exportScopeTag;

    /// <summary>主菜单导出范围：当前选中客户/产品 → 该范围；否则全部线索。</summary>
    private List<TicketThread> ResolveExportScope(out string scopeName)
    {
        if (_exportScopeTag is CustomerGroupViewModel or ProductGroupViewModel)
            return CollectScopeThreads(_exportScopeTag, out scopeName)!;
        return CollectAllThreads(out scopeName);
    }

    /// <summary>主菜单“导出”菜单项文本随当前选中范围动态更新。</summary>
    private void UpdateExportMenuText()
    {
        var (csv, xlsx) = _exportScopeTag switch
        {
            CustomerGroupViewModel cg => ($"导出该企业（{cg.Name}）的线索为 CSV", $"导出该企业（{cg.Name}）的线索为 Excel"),
            ProductGroupViewModel pg => ($"导出该产品（{pg.Name}）的线索为 CSV", $"导出该产品（{pg.Name}）的线索为 Excel"),
            _ => ("导出全部线索为 CSV", "导出全部线索为 Excel")
        };
        ExportCsvMenuItem.Header = csv;
        ExportExcelMenuItem.Header = xlsx;
    }

    /// <summary>导出右键范围（客户/产品）内的所有线索为 Excel，按状态给整行着色。</summary>
    private void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var threads = CollectScopeThreads(mi.Tag, out var scopeName);
        if (threads == null) return;
        ExportExcelCore(threads, scopeName);
    }

    /// <summary>主菜单：按当前选中范围（客户/产品）导出线索为 Excel，无选中则全部。</summary>
    private void ExportAllExcel_Click(object sender, RoutedEventArgs e)
        => ExportExcelCore(ResolveExportScope(out var scopeName), scopeName);

    /// <summary>Excel 导出主体：对话框 + 写 xlsx（按状态整行着色）+ 完成提示。</summary>
    private void ExportExcelCore(List<TicketThread> threads, string scopeName)
    {
        if (threads.Count == 0)
        {
            MessageBox.Show(this, "该范围内没有线索可导出。", "导出 Excel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出线索为 Excel",
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            FileName = $"线索导出_{SanitizeFileName(scopeName)}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("线索");
            string[] headers = { "工单号", "线索名称", "客户", "产品", "开始时间", "最后更新时间", "AI状态", "AI详情" };
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];
            var headerRange = ws.Range(1, 1, 1, headers.Length);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Font.FontColor = XLColor.White;
            headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3A5F");
            headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            int r = 2;
            foreach (var t in threads)
            {
                var values = new[]
                {
                    t.TicketNumber, RootSubject(t), t.Enterprise,
                    string.IsNullOrWhiteSpace(t.Product) ? "未分类产品" : t.Product,
                    t.FirstActivity.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    t.LastActivity.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    t.Status, t.StatusSummary
                };
                for (int c = 0; c < values.Length; c++)
                    ws.Cell(r, c + 1).Value = values[c];
                var fill = StatusFillColor(t.Status);
                if (fill != null)
                    ws.Range(r, 1, r, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml(fill);
                r++;
            }
            ws.Columns().AdjustToContents();
            ws.Range(1, 1, r - 1, headers.Length).SetAutoFilter(); // 表头自动筛选
            wb.SaveAs(dlg.FileName);
            ShowExportComplete(dlg.FileName, threads.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出 Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>状态 → Excel 行底色（与界面 AI 总结淡色一致）；未知状态不着色。</summary>
    private static string? StatusFillColor(string status) => status switch
    {
        "已解决" => "#E8F5E9",
        "等待客户回复" => "#FFEBEE",
        "等待客服回复" => "#FFEBEE",
        "等待研发回复" => "#E3F2FD",
        "纳入开发计划" => "#F3E5F5",
        "合并或拆分为其他工单" => "#F0FDFA",
        "处理中" => "#ECEFF1",
        "需升级" => "#FFEBEE",
        "新建" => "#F5F5F5",
        _ => null
    };

    /// <summary>导出成功提示框：显示导出条数与路径，可点击“打开文件”直接打开 CSV。</summary>
    private void ShowExportComplete(string file, int count)
    {
        var win = new Window
        {
            Title = "导出完成",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"已导出 {count} 条线索到：\n{file}",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 18)
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new Button
                            {
                                Content = "打开文件", Width = 90, Padding = new Thickness(6, 3, 6, 3),
                                IsDefault = true, Margin = new Thickness(0, 0, 10, 0)
                            },
                            new Button { Content = "关闭", Width = 80, Padding = new Thickness(6, 3, 6, 3), IsCancel = true }
                        }
                    }
                }
            }
        };
        // 给按钮挂事件
        if (win.Content is StackPanel panel &&
            panel.Children[1] is StackPanel buttons)
        {
            if (buttons.Children[0] is Button openBtn)
                openBtn.Click += (_, _) => OpenExportedFile(file);
            if (buttons.Children[1] is Button closeBtn)
                closeBtn.Click += (_, _) => win.Close();
        }
        win.ShowDialog();
    }

    /// <summary>用系统默认程序打开导出的文件。</summary>
    private static void OpenExportedFile(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开文件：{ex.Message}", "打开文件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>线索名称 = 界面节点显示的标题（与 EmailNodeViewModel.Title 一致）：AI 翻译优先，否则剥离标签后的纯主题。</summary>
    private static string RootSubject(TicketThread t)
    {
        var root = t.DisplayRoots.Count > 0 && t.DisplayRoots[0].Email is { } e
            ? e
            : t.Emails.OrderBy(x => x.DateSent).FirstOrDefault();
        if (root == null) return "";
        if (!string.IsNullOrWhiteSpace(root.AiTitle)) return root.AiTitle;
        var parsed = TicketManager.Services.SubjectParser.Parse(root.Subject);
        return parsed != null && !string.IsNullOrWhiteSpace(parsed.Fault)
            ? parsed.Fault
            : root.Subject.Trim();
    }

    /// <summary>CSV 转义：含逗号/引号/换行时用双引号包裹，内部引号翻倍。</summary>
    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "全部" : s;
    }

    private int _newMailIndex = -1;

    /// <summary>收集树中所有新同步的邮件节点（按当前树遍历顺序）。</summary>
    private List<EmailNodeViewModel> CollectNewMailNodes()
    {
        var list = new List<EmailNodeViewModel>();
        foreach (var cust in _vm.Customers)
            foreach (var prod in cust.Products)
                foreach (var root in prod.Threads)
                    CollectNew(root, list);
        return list;
    }

    private static void CollectNew(EmailNodeViewModel n, List<EmailNodeViewModel> list)
    {
        if (n.IsNew) list.Add(n);
        foreach (var c in n.Children) CollectNew(c, list);
    }

    /// <summary>增量合并（同步/排序）后：重新在 TreeView 中选中并滚动到恢复的选中节点，保持选中不变。</summary>
    private void RestoreSelectionInTree()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_vm.SelectedEmail is { } ev) SelectAndScrollToEmail(ev);
        }));
    }

    /// <summary>执行“跳转新邮件”：跳到下一条新同步的邮件并滚动选中（点击后自动清除其新标记）。</summary>
    private void JumpNewMail()
    {
        var nodes = CollectNewMailNodes();
        if (nodes.Count == 0)
        {
            _newMailIndex = -1;
            _vm.StatusText = "没有新同步的邮件";
            return;
        }
        _newMailIndex = (_newMailIndex + 1) % nodes.Count;
        var node = nodes[_newMailIndex];
        SelectAndScrollToEmail(node);
        _vm.StatusText = $"已跳转到新邮件（{_newMailIndex + 1}/{nodes.Count}）";
    }

    /// <summary>展开所在 客户/产品 分组并滚动选中指定邮件节点。</summary>
    private void SelectAndScrollToEmail(EmailNodeViewModel node)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            foreach (var cust in _vm.Customers)
            {
                bool found = false;
                foreach (var prod in cust.Products)
                {
                    if (!prod.Threads.Any(t => ContainsNode(t, node))) continue;
                    found = true;
                    if (TreeView.ItemContainerGenerator.ContainerFromItem(cust) is TreeViewItem cTvi)
                    {
                        cTvi.IsExpanded = true;
                        if (cTvi.ItemContainerGenerator.ContainerFromItem(prod) is TreeViewItem pTvi)
                            pTvi.IsExpanded = true;
                    }
                    break;
                }
                if (found) break;
            }
            // 展开分组后，子节点的 TreeViewItem 容器是异步生成的：
            // 逐轮展开目标邮件节点的祖先链、强制布局并定位选中；容器未就绪时重试数轮，
            // 直到定位成功（树刚重建后容器生成需要多轮布局），否则新分组折叠、用户看不到变化
            RetrySelectAndScroll(node, 8);
        }));
    }

    /// <summary>展开目标邮件祖先链 + 强制布局 + 定位选中；容器未就绪时重试，确保最终定位成功。</summary>
    private void RetrySelectAndScroll(EmailNodeViewModel node, int attemptsLeft)
    {
        if (attemptsLeft <= 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ExpandEmailAncestors(TreeView, node);
            TreeView.UpdateLayout();
            if (!SelectAndScroll(TreeView, node))
                RetrySelectAndScroll(node, attemptsLeft - 1);
        }));
    }

    /// <summary>展开包含目标邮件的祖先邮件节点，确保其子容器生成、目标可见可定位。</summary>
    private static void ExpandEmailAncestors(ItemsControl parent, EmailNodeViewModel target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
            if (item is EmailNodeViewModel ev && !ReferenceEquals(ev, target) && ContainsNode(ev, target))
            {
                tvi.IsExpanded = true;
                ExpandEmailAncestors(tvi, target);
                return;
            }
            ExpandEmailAncestors(tvi, target);
        }
    }

    private static bool ContainsNode(EmailNodeViewModel root, EmailNodeViewModel target)
    {
        if (ReferenceEquals(root, target)) return true;
        return root.Children.Any(c => ContainsNode(c, target));
    }

    /// <summary>在 TreeView 中定位并选中、滚动到指定邮件节点。</summary>
    private static bool SelectAndScroll(ItemsControl parent, EmailNodeViewModel target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
            if (item is EmailNodeViewModel ev && ReferenceEquals(ev, target))
            {
                if (tvi.Parent is TreeViewItem ptvi) ptvi.IsExpanded = true;
                tvi.IsSelected = true;
                // 仅当目标不在可视区域内才滚动：已可见的节点保持原位，避免重建后抖动/位置变化
                ScrollIntoViewIfNeeded(tvi);
                return true;
            }
            if (SelectAndScroll(tvi, target)) return true;
        }
        return false;
    }

    /// <summary>仅当目标 TreeViewItem 不在 TreeView 可视区域内时才 BringIntoView（保持已可见节点的显示位置）。</summary>
    private static void ScrollIntoViewIfNeeded(TreeViewItem tvi)
    {
        var sv = FindTreeScrollViewer(tvi);
        if (sv == null) { tvi.BringIntoView(); return; }
        try
        {
            tvi.UpdateLayout();
            var p = tvi.TransformToAncestor(sv).Transform(new Point(0, 0));
            double top = p.Y;
            double bottom = top + tvi.ActualHeight;
            if (top >= 0 && bottom <= sv.ViewportHeight) return; // 已完整可见 → 不滚动
        }
        catch { /* 布局未就绪，按需滚动兜底 */ }
        tvi.BringIntoView();
    }

    /// <summary>从节点向上查找最近的 ScrollViewer（TreeView 内部的滚动容器）。</summary>
    private static ScrollViewer? FindTreeScrollViewer(DependencyObject d)
    {
        while (d != null && d is not ScrollViewer)
            d = VisualTreeHelper.GetParent(d);
        return d as ScrollViewer;
    }



    private async void RegenerateStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        await _vm.RegenerateThreadStatusAsync(ev.Email.Id);
    }

    /// <summary>右键“全部已读”：把该线索内所有新同步邮件标记为已读。</summary>
    /// <summary>视图菜单“智能整理”：已结束的线索/产品/企业收起，进行中的（需关注的）展开。</summary>
    private void SmartCollapse_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait; // 智能整理/展开较耗时，先显示忙碌光标
        try
        {
            void Walk(ItemsControl parent)
            {
                foreach (var item in parent.Items)
                {
                    if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
                    bool? expand = item switch
                    {
                        CustomerGroupViewModel c => !IsCustomerAllClosed(c),   // 有进行中 → 展开；全部结束 → 收起
                        ProductGroupViewModel p => !IsProductAllClosed(p),
                        // 线索：仅“未结束 且 有新邮件”才展开；无新邮件的进行中线索也保持折叠
                        EmailNodeViewModel ev when ev.IsRoot => !MainViewModel.IsThreadClosed(ev.ThreadOwner.Thread) && ev.ThreadOwner.HasNewMail,
                        _ => null
                    };
                    if (expand.HasValue) tvi.IsExpanded = expand.Value;
                    Walk(tvi);
                }
            }
            Walk(TreeView);
            _vm.StatusText = "已智能整理：结束的收起、进行中的展开";
            // 等布局/渲染完成后再恢复光标（展开大量节点时渲染较耗时）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                TreeView.UpdateLayout();
                Mouse.OverrideCursor = null;
            }));
        }
        catch
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>产品下所有线索都已结束。</summary>
    private static bool IsProductAllClosed(ProductGroupViewModel p)
        => p.Threads.Count > 0 && p.Threads.All(r => MainViewModel.IsThreadClosed(r.ThreadOwner.Thread));

    /// <summary>企业下所有产品都已结束。</summary>
    private static bool IsCustomerAllClosed(CustomerGroupViewModel c)
        => c.Products.Count > 0 && c.Products.All(IsProductAllClosed);

    /// <summary>企业行右键“重命名企业”：先让 AI 推断正式名称候选供选择，再把该企业下所有线索的企业名称改为新名称（产品不变）。</summary>
    private async void RenameEnterprise_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not CustomerGroupViewModel cust) return;
        var roots = cust.Products.SelectMany(p => p.Threads).ToList();
        if (roots.Count == 0) return;
        var rootEmailIds = roots.Select(r => r.Email.Id).ToList();

        // 用 AI 查询该企业可能对应的正式名称候选（连同相关域名一并提供，失败则无候选，仍可手动输入）
        var suggestions = new List<string>();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _vm.StatusText = "正在查询企业正式名称候选…";
            await Task.Yield();
            var relatedDomains = CollectDomainsForEnterprise(cust);
            using var ai = new TicketManager.Services.DeepSeekService(App.Workflow.Config);
            var list = await ai.SuggestEnterpriseNamesAsync(cust.Name, relatedDomains);
            if (list != null) suggestions = list;
        }
        catch { /* AI 失败则无候选 */ }
        finally { Mouse.OverrideCursor = null; }

        var box = new TextBox { Text = cust.Name, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4) };
        var listBox = new ListBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            MaxHeight = 150,
            ItemsSource = suggestions,
            Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        listBox.SelectionChanged += (_, _) => { if (listBox.SelectedItem is string s) box.Text = s; };
        var okBtn = new Button { Content = "确定", Width = 80, Padding = new Thickness(6, 3, 6, 3), IsDefault = true };
        var cancelBtn = new Button { Content = "取消", Width = 80, Padding = new Thickness(6, 3, 6, 3), IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        var win = new Window
        {
            Title = "重命名企业",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"将把企业名称「{cust.Name}」改为：", TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = "AI 建议的正式名称（点选自动填入）：",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B7DE9")),
                        Margin = new Thickness(0, 12, 0, 0),
                        Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed
                    },
                    listBox,
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { okBtn, cancelBtn } }
                }
            }
        };
        okBtn.Click += (_, _) => win.DialogResult = true;
        cancelBtn.Click += (_, _) => win.DialogResult = false;
        if (win.ShowDialog() != true) return;
        var newName = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, cust.Name, StringComparison.Ordinal)) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Yield();
            // product 传空串 → 只更新企业，不改产品
            await Task.Run(() => _vm.SetMetaForThreads(rootEmailIds, "", newName));
            ReloadPreservingExpansion();
            _vm.StatusText = $"已把企业「{cust.Name}」更名为「{newName}」（{roots.Count} 条线索）";
        }
        catch (Exception ex)
        {
            _vm.StatusText = "更名失败：" + ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>收集某企业相关的邮箱域名：域名→企业映射表反查 + 该企业下所有线索邮件参与者的域名（去重）。</summary>
    private static List<string> CollectDomainsForEnterprise(CustomerGroupViewModel cust)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1) 域名→企业 映射表反查（该企业对应的域名）
        foreach (var kv in App.Workflow.Config.DomainEnterpriseMappings)
            if (string.Equals(kv.Value, cust.Name, StringComparison.OrdinalIgnoreCase))
                domains.Add(kv.Key);
        // 2) 该企业下所有线索邮件的参与者域名
        foreach (var p in cust.Products)
            foreach (var root in p.Threads)
                foreach (var email in root.ThreadOwner.Thread.Emails)
                {
                    AddAddressDomains(domains, email.FromAddress);
                    AddAddressDomains(domains, email.ToAddresses);
                    AddAddressDomains(domains, email.CcAddresses);
                }
        return domains.OrderBy(x => x).Take(20).ToList();
    }

    /// <summary>把地址串（分号/逗号分隔）中的邮箱域名加入集合。</summary>
    private static void AddAddressDomains(HashSet<string> set, string addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses)) return;
        foreach (var addr in addresses.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var at = addr.IndexOf('@');
            if (at >= 0 && at < addr.Length - 1)
            {
                var d = addr[(at + 1)..].Trim().ToLowerInvariant();
                if (d.Length > 0) set.Add(d);
            }
        }
    }

    /// <summary>客户/产品右键菜单打开时：填充“设置产品/设置客户”子菜单，统一应用到该范围内所有线索。</summary>
    private void GroupContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var roots = new List<EmailNodeViewModel>();
        switch (menu.DataContext)
        {
            case CustomerGroupViewModel cust:
                foreach (var p in cust.Products)
                    foreach (var root in p.Threads) roots.Add(root);
                break;
            case ProductGroupViewModel prod:
                foreach (var root in prod.Threads) roots.Add(root);
                break;
            default:
                return;
        }
        if (roots.Count == 0) return;
        var targets = roots.Select(r => r.Email.ThreadId).ToList();
        var ev = roots[0]; // 用首个线索作为“当前值”参考（勾选显示）
        var setProduct = FindMenuItem(menu, "设置产品");
        var setEnterprise = FindMenuItem(menu, "设置客户");
        if (setProduct != null) PopulateMetaMenu(setProduct, targets, ev, isProduct: true);
        if (setEnterprise != null) PopulateMetaMenu(setEnterprise, targets, ev, isProduct: false);
    }

    /// <summary>客户/产品右键“全部已读”：把该范围内所有线索的所有邮件标记为已读。</summary>
    private void GroupSeen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var roots = new List<EmailNodeViewModel>();
        switch (mi.Tag)
        {
            case CustomerGroupViewModel cust:
                foreach (var p in cust.Products)
                    foreach (var root in p.Threads) roots.Add(root);
                break;
            case ProductGroupViewModel prod:
                foreach (var root in prod.Threads) roots.Add(root);
                break;
            default:
                return;
        }
        if (roots.Count == 0) return;
        _vm.MarkGroupSeen(roots);
        _vm.StatusText = "已将该范围内的所有线索标记为已读";
    }

    private void MarkThreadSeen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        _vm.MarkThreadSeen(ev.Email.ThreadId);
        _vm.StatusText = "已将该线索全部邮件标记为已读";
    }

    /// <summary>右键“复制工单号”：把当前线索的工单号复制到剪贴板。</summary>
    private void CopyTicketNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        var ticket = ev.ThreadOwner.TicketNumber;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            _vm.StatusText = "该线索没有工单号";
            return;
        }
        Clipboard.SetText(ticket);
        _vm.StatusText = $"已复制工单号：{ticket}";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version?.ToString(3) ?? "?";
        // 构建号：以可执行文件的编译时间作为构建时间戳
        var build = File.Exists(asm.Location)
            ? File.GetLastWriteTime(asm.Location).ToString("yyyyMMdd.HHmm")
            : "?";
        MessageBox.Show(this,
            "工单邮件管理器\n\n" +
            $"版本 {ver}（构建 {build}）\n\n" +
            "通过 Zoho Mail REST API 同步工单邮件，自动解析主题、按工单归组线程，" +
            "并用 AI 提炼标题、总结工单状态，按 客户/产品 组织展示。",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>重建树时保留各节点的展开/折叠状态、滚动位置，保持“当前选中”线索选中且显示位置不变。</summary>
    private void ReloadPreservingExpansion()
    {
        var expanded = new HashSet<string>();
        CaptureExpanded(TreeView, "", expanded);
        // 记录当前滚动位置：重建后恢复，保持原可视区域（不滚动到选中，避免窗口抖动/位置跳变）
        double prevOffset = -1;
        if (FindScrollViewer(TreeView) is { } sv) prevOffset = sv.VerticalOffset;
        _vm.Reload();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            RestoreExpanded(TreeView, "", expanded);
            // 展开状态恢复后，多轮把滚动位置恢复到原位，让当前窗口显示内容（含选中线索）保持不变
            RestoreScrollOffset(prevOffset);
        }));
    }

    /// <summary>在布局稳定后把 TreeView 滚动位置恢复到指定偏移（容器延迟生成，需多轮重试直到生效）。</summary>
    private void RestoreScrollOffset(double offset, int attempts = 8)
    {
        if (offset < 0 || attempts <= 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (FindScrollViewer(TreeView) is { } sv)
            {
                sv.ScrollToVerticalOffset(offset);
                // 目标偏移尚未生效（容器/布局未就绪），继续重试
                if (Math.Abs(sv.VerticalOffset - offset) > 0.5)
                    RestoreScrollOffset(offset, attempts - 1);
            }
        }));
    }

    /// <summary>向下查找第一个 ScrollViewer（TreeView 的滚动容器）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    private static string KeyOf(object item) => item switch
    {
        CustomerGroupViewModel c => "c:" + c.Name,
        ProductGroupViewModel p => "p:" + p.Name,
        EmailNodeViewModel e => "e:" + e.Email.Id,
        _ => "?"
    };

    private static void CaptureExpanded(ItemsControl items, string prefix, HashSet<string> set)
    {
        foreach (var item in items.Items)
        {
            var key = prefix + "/" + KeyOf(item);
            if (items.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                if (tvi.IsExpanded)
                {
                    set.Add(key);
                    CaptureExpanded(tvi, key, set);
                }
            }
        }
    }

    private static void RestoreExpanded(ItemsControl items, string prefix, HashSet<string> set)
    {
        foreach (var item in items.Items)
        {
            var key = prefix + "/" + KeyOf(item);
            if (items.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                if (set.Contains(key)) tvi.IsExpanded = true;
                RestoreExpanded(tvi, key, set);
            }
        }
    }
}
