using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
            // 数据写入 + 线程重建（耗时）放到后台线程，避免阻塞 UI 导致刷新时空白/卡顿；
            // 用根邮件 Id 定位（稳定），避免界面 ThreadId 过期导致更新 0 行
            await Task.Run(() => _vm.SetMetaForThreads(rootEmailIds, product, enterprise));
            // 回到 UI 线程重建显示
            ReloadPreservingExpansion();
            _vm.ReselectByRootEmailIds(rootEmailIds); // 重建后按根邮件重新高亮选中
            MetaLog($"Reload后 目标分组=" + (rootEmailIds.Count > 0 ? FindGroupOfRoot(rootEmailIds[0]) : "无"));
            // 尽力滚动选中目标线索（多轮容器就绪等待）；失败不影响可见性（分组已默认展开）
            if (rootEmailIds.Count > 0) SelectRootEmailById(rootEmailIds[0]);
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
                tvi.BringIntoView();
                return true;
            }
            if (SelectAndScroll(tvi, target)) return true;
        }
        return false;
    }



    private async void RegenerateStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        await _vm.RegenerateThreadStatusAsync(ev.Email.Id);
    }

    /// <summary>右键“全部已读”：把该线索内所有新同步邮件标记为已读。</summary>
    private void MarkThreadSeen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        _vm.MarkThreadSeen(ev.Email.ThreadId);
        _vm.StatusText = "已将该线索全部邮件标记为已读";
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
            "从 IMAP 邮箱同步工单邮件，自动解析主题、按工单归组线程，" +
            "并用 AI 提炼标题、总结工单状态，按 客户/产品 组织展示。\n\n" +
            $"数据库：{_vm.DbPath}",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>重建树时保留各节点的展开/折叠状态，避免设置后全部收起。</summary>
    private void ReloadPreservingExpansion()
    {
        var expanded = new HashSet<string>();
        CaptureExpanded(TreeView, "", expanded);
        _vm.Reload();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestoreExpanded(TreeView, "", expanded)));
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
