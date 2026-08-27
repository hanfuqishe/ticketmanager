using System.Windows;
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
        if (Application.Current.MainWindow is { IsVisible: true } owner)
            Owner = owner;
    }
}
