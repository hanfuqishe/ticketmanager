using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TicketManager.Services;
using TicketManager.ViewModels;

namespace TicketManager.Views;

/// <summary>回复邮件窗口：可选收信人=客服/客户接口人（另一个抄送），引用只带头部，可一键翻译成英文。</summary>
public partial class ReplyTicketWindow : Window
{
    private readonly ReplyTicketViewModel _vm;

    public ReplyTicketWindow(WorkflowService workflow, Models.EmailMessage email)
    {
        InitializeComponent();
        _vm = new ReplyTicketViewModel(workflow, email);
        DataContext = _vm;
        _vm.Sended += Close;
        // 覆盖正文框的 Paste 命令：剪贴板含 文件/图片 时右键“粘贴”也可用（TextBox 默认只认文本会置灰），执行时转附件
        BodyBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, BodyPaste_Executed, BodyPaste_CanExecute));
        if (Application.Current.MainWindow is { IsVisible: true } owner)
            Owner = owner;
    }

    /// <summary>添加附件（可多选）。</summary>
    private void AddAttachment_Click(object sender, RoutedEventArgs e) => _vm.AddAttachment();

    /// <summary>移除选中的附件。</summary>
    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
        => _vm.RemoveAttachment(AttachmentList.SelectedItem);

    /// <summary>额外抄送选择我方同事：追加到额外抄送栏（去重）。</summary>
    private void CcColleagueBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string item) return;
        _vm.AppendColleagueCc(item);
        cb.SelectedItem = null; // 允许再次选择其他同事
    }

    /// <summary>正文框 Paste 命令可用性：剪贴板含 文本/文件/图片 即可粘贴（文件/图片会转附件，不再因无文本而置灰）。</summary>
    private void BodyPaste_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        var data = Clipboard.GetDataObject();
        e.CanExecute = data != null &&
            (data.GetDataPresent(DataFormats.UnicodeText) ||
             data.GetDataPresent(DataFormats.Text) ||
             data.GetDataPresent(DataFormats.FileDrop) ||
             data.GetDataPresent(DataFormats.Bitmap));
        e.Handled = true;
    }

    /// <summary>正文框粘贴执行：文件/图片转附件；纯文本正常粘贴。</summary>
    private void BodyPaste_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var data = Clipboard.GetDataObject();
        if (data == null) return;
        // 1) 文件（资源管理器复制/剪切文件）
        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var added = 0;
            foreach (var f in files)
                if (System.IO.File.Exists(f)) { _vm.AddAttachmentPath(f); added++; }
            if (added > 0)
            {
                _vm.StatusText = $"已将 {added} 个文件添加为附件";
                return;
            }
        }
        // 2) 图片（浏览器/截图复制）
        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var saved = SavePastedImage(data);
            if (saved != null)
            {
                _vm.AddAttachmentPath(saved);
                _vm.StatusText = "已将粘贴的图片添加为附件";
                return;
            }
        }
        // 3) 纯文本：走默认粘贴
        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            BodyBox.Paste();
    }

    /// <summary>把剪贴板位图保存为临时 PNG，返回路径；失败返回 null。</summary>
    private static string? SavePastedImage(IDataObject data)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TicketManager_{Guid.NewGuid():N}.png");
            if (data.GetData(DataFormats.Bitmap) is System.Drawing.Bitmap bmp)
            {
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return path;
            }
            if (data.GetData("System.Windows.Media.Imaging.BitmapSource") is System.Windows.Media.Imaging.BitmapSource src)
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                using (var fs = System.IO.File.Create(path)) encoder.Save(fs);
                return path;
            }
        }
        catch { }
        return null;
    }
}
