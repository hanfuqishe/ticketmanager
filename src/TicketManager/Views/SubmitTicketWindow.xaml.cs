using System.Windows;
using System.Windows.Controls;
using TicketManager.Services;
using TicketManager.ViewModels;

namespace TicketManager.Views;

public partial class SubmitTicketWindow : Window
{
    private readonly SubmitTicketViewModel _vm;

    public SubmitTicketWindow(WorkflowService workflow)
    {
        InitializeComponent();
        _vm = new SubmitTicketViewModel(workflow);
        DataContext = _vm;
        _vm.Sended += Close;
        // 仅当主窗口已显示才设 Owner（与 SettingsWindow 一致）
        if (Application.Current.MainWindow is { IsVisible: true } owner)
            Owner = owner;
    }

    /// <summary>客户下拉失焦：刷新该客户的接口人列表。</summary>
    private void CustomerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _vm.LoadContacts();
    }

    /// <summary>客户下拉选中变化：立即刷新该客户的接口人列表（可编辑下拉 Text 绑定可能滞后，先同步选中项）。</summary>
    private void CustomerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomerBox.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
            _vm.Customer = s; // 确保与选中项同步，再按客户刷新接口人
        _vm.LoadContacts();
    }

    /// <summary>抄送栏选择我方同事：追加到抄送列表（逗号分隔、去重）。</summary>
    private void CcColleagueBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string item) return;
        var emails = (_vm.CcEmails ?? "").Split(';', ',')
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var email = WorkflowService.ExtractEmail(item);
        if (!emails.Any(x => string.Equals(WorkflowService.ExtractEmail(x), email, StringComparison.OrdinalIgnoreCase)))
        {
            emails.Add(item);
            _vm.CcEmails = string.Join(", ", emails);
        }
        cb.SelectedItem = null; // 允许再次选择其他同事
    }

    /// <summary>添加附件（可多选）。</summary>
    private void AddAttachment_Click(object sender, RoutedEventArgs e) => _vm.AddAttachment();

    /// <summary>移除选中的附件。</summary>
    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
        => _vm.RemoveAttachment(AttachmentList.SelectedItem);
}

/// <summary>把附件完整路径转换为 “文件名 (大小)” 显示；悬停 tooltip 仍显示完整路径。</summary>
public class AttachmentDisplayConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string path && System.IO.File.Exists(path))
        {
            var name = System.IO.Path.GetFileName(path);
            var size = new System.IO.FileInfo(path).Length;
            return $"{name} ({FormatSize(size)})";
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1048576) return $"{bytes / 1048576.0:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }
}
