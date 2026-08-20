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

    private bool _enableAutoSync;
    public bool EnableAutoSync { get => _enableAutoSync; set => Set(ref _enableAutoSync, value); }

    public ICommand AddAddressCommand { get; }
    public ICommand RemoveAddressCommand { get; }

    public ObservableCollection<string> DomainMappings { get; } = new();

    private string _newDomain = "";
    public string NewDomain { get => _newDomain; set => Set(ref _newDomain, value); }

    private string _newEnterprise = "";
    public string NewEnterprise { get => _newEnterprise; set => Set(ref _newEnterprise, value); }

    public ICommand AddMappingCommand { get; }
    public ICommand RemoveMappingCommand { get; }

    public SettingsViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        _config = workflow.LoadConfig();
        foreach (var a in _config.MonitoredAddresses)
            MonitoredAddresses.Add(a);
        EnableAutoSync = _config.EnableAutoSync;

        var idx = Array.IndexOf(ProxyTypes, _config.ProxyType);
        SelectedProxyIndex = idx < 0 ? 1 : idx;

        AddAddressCommand = new RelayCommand(_ => AddAddress());
        RemoveAddressCommand = new RelayCommand(p =>
        {
            if (p is string s) MonitoredAddresses.Remove(s);
        });

        foreach (var kv in _config.DomainEnterpriseMappings)
            DomainMappings.Add($"{kv.Key} → {kv.Value}");

        AddMappingCommand = new RelayCommand(_ => AddMapping());
        RemoveMappingCommand = new RelayCommand(p =>
        {
            if (p is string s) DomainMappings.Remove(s);
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

    private void AddMapping()
    {
        var d = NewDomain.Trim().ToLowerInvariant();
        var en = NewEnterprise.Trim();
        if (d.Length == 0 || en.Length == 0) return;
        if (!DomainMappings.Any(x => x.StartsWith(d + " → ", StringComparison.OrdinalIgnoreCase)))
            DomainMappings.Add($"{d} → {en}");
        NewDomain = "";
        NewEnterprise = "";
    }

    public void Save()
    {
        _config.MonitoredAddresses = MonitoredAddresses.ToList();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in DomainMappings)
        {
            var idx = entry.IndexOf(" → ");
            if (idx < 0) continue;
            var d = entry[..idx].Trim();
            var en = entry[(idx + 3)..].Trim();
            if (d.Length > 0 && en.Length > 0) dict[d] = en;
        }
        _config.DomainEnterpriseMappings = dict;
        if (SelectedProxyIndex >= 0 && SelectedProxyIndex < ProxyTypes.Length)
            _config.ProxyType = ProxyTypes[SelectedProxyIndex];
        _config.EnableAutoSync = EnableAutoSync;
        _workflow.SaveConfig(_config);
    }
}
