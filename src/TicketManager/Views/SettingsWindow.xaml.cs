using System.Windows;
using TicketManager.Services;
using TicketManager.ViewModels;

namespace TicketManager.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(WorkflowService workflow)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(workflow);
        DataContext = _vm;
        ImapPasswordBox.Password = _vm.Config.ImapPassword;
        DeepSeekKeyBox.Password = _vm.Config.DeepSeekApiKey;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Config.ImapPassword = ImapPasswordBox.Password;
        _vm.Config.DeepSeekApiKey = DeepSeekKeyBox.Password;
        _vm.Save();
        DialogResult = true;
    }
}
