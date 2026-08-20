using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
        _vm.AutoSyncAndListen(); // 启动后自动同步，随后进入自动收取新邮件模式
    }

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
        switch (e.NewValue)
        {
            case ThreadViewModel tv:
                _vm.SelectThread(tv);
                break;
            case EmailNodeViewModel ev:
                _vm.SelectEmail(ev);
                break;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = "";
    }

    /// <summary>右键菜单打开时，动态填充「设置产品/设置客户」子菜单的候选列表。</summary>
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not EmailNodeViewModel ev) return;
        var setProduct = FindMenuItem(menu, "设置产品");
        var setEnterprise = FindMenuItem(menu, "设置客户");
        if (setProduct != null) PopulateMetaMenu(setProduct, ev, isProduct: true);
        if (setEnterprise != null) PopulateMetaMenu(setEnterprise, ev, isProduct: false);
    }

    private static MenuItem? FindMenuItem(ItemsControl parent, string header) =>
        parent.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == header);

    private void PopulateMetaMenu(MenuItem sub, EmailNodeViewModel ev, bool isProduct)
    {
        sub.Items.Clear();
        var names = isProduct ? App.Workflow.GetKnownProducts() : App.Workflow.GetKnownEnterprises();
        var current = isProduct ? ev.Email.Product : ev.Email.Enterprise;
        foreach (var name in names)
        {
            var mi = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(name, current, StringComparison.Ordinal),
                Tag = (ev, name, isProduct)
            };
            mi.Click += SetMetaDirect_Click;
            sub.Items.Add(mi);
        }
        sub.IsEnabled = names.Count > 0;
    }

    private void SetMetaDirect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not (EmailNodeViewModel ev, string name, bool isProduct)) return;
        var product = isProduct ? name : ev.Email.Product;
        var enterprise = isProduct ? ev.Email.Enterprise : name;
        App.Workflow.SetEmailMeta(ev.Email.Id, product, enterprise);
        ReloadPreservingExpansion();
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
