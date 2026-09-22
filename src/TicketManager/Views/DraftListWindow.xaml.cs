using System.Linq;
using System.Windows;
using System.Windows.Input;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.Views;

/// <summary>本地草稿列表窗口：选择一份草稿载入编辑窗口，或删除。由发送窗口的“草稿…”按钮打开。</summary>
public partial class DraftListWindow : Window
{
    private readonly string _kind; // submit / reply：只显示同类草稿

    /// <summary>用户选择的草稿（点“载入”/双击后有效，调用方据此填充编辑窗口）。</summary>
    public EmailDraft? SelectedDraft { get; private set; }

    public DraftListWindow(string kind)
    {
        InitializeComponent();
        _kind = kind;
        Refresh();
    }

    private void Refresh()
    {
        var all = DraftService.LoadAll();
        DraftList.ItemsSource = string.IsNullOrEmpty(_kind)
            ? all
            : all.Where(d => d.Kind == _kind).ToList();
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (DraftList.SelectedItem is not EmailDraft d) return;
        SelectedDraft = d;
        DialogResult = true;
    }

    private void DraftList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Load_Click(sender, e);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DraftList.SelectedItem is not EmailDraft d) return;
        if (MessageBox.Show(this, $"删除草稿“{d.Title}”？", "删除草稿",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        DraftService.Delete(d.Id);
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
