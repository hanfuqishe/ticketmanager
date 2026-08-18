using System.Collections.ObjectModel;
using System.Windows.Input;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly WorkflowService _workflow;
    private AppConfig _config;

    public AppConfig Config => _config;

    public ObservableCollection<string> MonitoredAddresses { get; } = new();

    public string[] ProxyTypes { get; } = { "Socks4", "Socks5", "Http" };

    private int _selectedProxyIndex = 1;
    public int SelectedProxyIndex { get => _selectedProxyIndex; set => Set(ref _selectedProxyIndex, value); }

    private string _newAddress = "";
    public string NewAddress { get => _newAddress; set => Set(ref _newAddress, value); }

    public string DbPath => App.Db.DbPath;

    public ICommand AddAddressCommand { get; }
    public ICommand RemoveAddressCommand { get; }

    public SettingsViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        _config = workflow.LoadConfig();
        foreach (var a in _config.MonitoredAddresses)
            MonitoredAddresses.Add(a);

        var idx = Array.IndexOf(ProxyTypes, _config.ProxyType);
        SelectedProxyIndex = idx < 0 ? 1 : idx;

        AddAddressCommand = new RelayCommand(_ => AddAddress());
        RemoveAddressCommand = new RelayCommand(p =>
        {
            if (p is string s) MonitoredAddresses.Remove(s);
        });
    }

    private void AddAddress()
    {
        var a = NewAddress.Trim();
        if (a.Length == 0) return;
        if (!MonitoredAddresses.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
            MonitoredAddresses.Add(a);
        NewAddress = "";
    }

    public void Save()
    {
        _config.MonitoredAddresses = MonitoredAddresses.ToList();
        if (SelectedProxyIndex >= 0 && SelectedProxyIndex < ProxyTypes.Length)
            _config.ProxyType = ProxyTypes[SelectedProxyIndex];
        _workflow.SaveConfig(_config);
    }
}
