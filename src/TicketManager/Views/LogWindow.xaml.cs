using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TicketManager.ViewModels;

namespace TicketManager.Views;

/// <summary>运行日志窗口：显示状态栏（窗口最下行）消息的完整历史。默认不打开，由主菜单“视图 → 运行日志…”打开。</summary>
public partial class LogWindow : Window
{
    private readonly MainViewModel _vm;

    public LogWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Logs.CollectionChanged += OnLogsChanged;
        // 打开时直接定位到最新一条
        if (vm.Logs.Count > 0)
            LogList.ScrollIntoView(vm.Logs[^1]);
    }

    /// <summary>有新日志时自动滚动到最新（仅新增动作，清空/移除不处理）。</summary>
    private void OnLogsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        if (e.NewItems == null || e.NewItems.Count == 0) return;
        var last = e.NewItems[^1];
        if (last != null) LogList.ScrollIntoView(last);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Logs.CollectionChanged -= OnLogsChanged;
        base.OnClosed(e);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _vm.ClearLogs();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join(Environment.NewLine, _vm.Logs.Select(l => $"{l.Time}  {l.Message}"));
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", "运行日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
