using System.Collections.Generic;
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

    private void SetMetaDirect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not (List<long> targets, string name, bool isProduct)) return;
        // 先记录被设置线索的根邮件 Id（线程重建后 ThreadId 会变，邮件 Id 稳定），用于保持选中
        var rootEmailIds = _vm.GetRootEmailIds(targets);
        _vm.SetMetaForThreads(targets, isProduct ? name : "", isProduct ? "" : name);
        ReloadPreservingExpansion();
        _vm.ReselectByRootEmailIds(rootEmailIds); // 重建后按根邮件重新高亮选中
        ExpandSelectedThreads();                   // 展开所在分组并滚动，保持可见
        _vm.StatusText = $"已为 {targets.Count} 条线索设置 {(isProduct ? "产品：" + name : "客户：" + name)}";
    }

    /// <summary>展开包含选中线索的 客户/产品 分组，并滚动到第一条选中线索，保证可见。</summary>
    private void ExpandSelectedThreads()
    {
        var sel = _vm.SelectedThreadIds;
        if (sel.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            foreach (var cust in _vm.Customers)
            {
                if (cust.Products.All(p => !p.Threads.Any(t => sel.Contains(t.Email.ThreadId)))) continue;
                if (TreeView.ItemContainerGenerator.ContainerFromItem(cust) is not TreeViewItem cTvi) continue;
                cTvi.IsExpanded = true;
                foreach (var prod in cust.Products)
                {
                    if (!prod.Threads.Any(t => sel.Contains(t.Email.ThreadId))) continue;
                    if (cTvi.ItemContainerGenerator.ContainerFromItem(prod) is TreeViewItem pTvi)
                        pTvi.IsExpanded = true;
                }
            }
            ScrollToThread(sel.First());
        }));
    }

    /// <summary>滚动树视图使指定线程线索可见。</summary>
    private void ScrollToThread(long threadId)
    {
        if (threadId <= 0) return;
        void Walk(ItemsControl parent)
        {
            foreach (var item in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
                if (item is EmailNodeViewModel ev && ev.IsRoot && ev.Email.ThreadId == threadId)
                {
                    tvi.BringIntoView();
                    return;
                }
                Walk(tvi);
            }
        }
        Walk(TreeView);
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
        _vm.SetThreadStatus(ev.Email.ThreadId, status);
    }

    private async void RegenerateStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not EmailNodeViewModel ev) return;
        await _vm.RegenerateThreadStatusAsync(ev.Email.ThreadId);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "工单邮件管理器\n\n" +
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
