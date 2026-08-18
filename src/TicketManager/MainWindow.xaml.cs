using System.Windows;
using TicketManager.ViewModels;

namespace TicketManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(App.Workflow);
        DataContext = _vm;
        _vm.Load();
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
}
