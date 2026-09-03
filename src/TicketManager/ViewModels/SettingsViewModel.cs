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

    public ObservableCollection<string> MySupportDomains { get; } = new();

    private string _newSupportDomain = "";
    public string NewSupportDomain { get => _newSupportDomain; set => Set(ref _newSupportDomain, value); }

    public ICommand AddSupportDomainCommand { get; }
    public ICommand RemoveSupportDomainCommand { get; }

    public string[] ProxyTypes { get; } = { "Socks4", "Socks5", "Http" };

    /// <summary>邮箱同步方式选项（显示值）。</summary>
    public string[] SyncModes { get; } = { "自动（Zoho 优先，否则 IMAP）", "Zoho REST API", "IMAP" };

    /// <summary>选中的同步方式（映射到 Config.SyncMode 的 Auto/Zoho/Imap）。</summary>
    public string SelectedSyncMode
    {
        get => _config.SyncMode switch
        {
            "Zoho" => SyncModes[1],
            "Imap" => SyncModes[2],
            _ => SyncModes[0]
        };
        set
        {
            _config.SyncMode = value switch
            {
                _ when value == SyncModes[1] => "Zoho",
                _ when value == SyncModes[2] => "Imap",
                _ => "Auto"
            };
            OnPropertyChanged(nameof(SelectedSyncMode));
        }
    }

    /// <summary>AI 提供商预设（OpenAI 兼容接口）。</summary>
    public List<AiProviderInfo> AiProviders => AiProviderPresets.All;

    /// <summary>选中的 AI 提供商：切换时自动填入接口地址与模型（可再手动改；「自定义」不改写）。</summary>
    public AiProviderInfo SelectedAiProvider
    {
        get => AiProviders.FirstOrDefault(p => p.Name == _config.AiProvider) ?? AiProviders[0];
        set
        {
            if (value == null || value.Name == _config.AiProvider) return;
            _config.AiProvider = value.Name;
            if (!string.IsNullOrEmpty(value.BaseUrl)) _config.DeepSeekBaseUrl = value.BaseUrl;
            if (!string.IsNullOrEmpty(value.Model)) _config.DeepSeekModel = value.Model;
            OnPropertyChanged(nameof(SelectedAiProvider));
            OnPropertyChanged(nameof(Config));
        }
    }

    private int _selectedProxyIndex = 1;
    public int SelectedProxyIndex { get => _selectedProxyIndex; set => Set(ref _selectedProxyIndex, value); }

    /// <summary>关注客服邮箱是否为空（为空时同步会采集往来中全部邮件，UI 提示用）。</summary>
    public bool IsMonitoredEmpty => MonitoredAddresses.Count == 0;

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

    /// <summary>产品简称编辑项（显示 “简称 → 全称”，如 “ue → UEM Central”）。</summary>
    public ObservableCollection<string> ProductAliasItems { get; } = new();

    private string _newAliasShort = "";
    public string NewAliasShort { get => _newAliasShort; set => Set(ref _newAliasShort, value); }

    private string _newAliasFull = "";
    public string NewAliasFull { get => _newAliasFull; set => Set(ref _newAliasFull, value); }

    public ICommand AddAliasCommand { get; }
    public ICommand RemoveAliasCommand { get; }

    public SettingsViewModel(WorkflowService workflow)
    {
        _workflow = workflow;
        _config = workflow.LoadConfig();
        foreach (var a in _config.MonitoredAddresses)
            MonitoredAddresses.Add(a);
        foreach (var d in _config.MySupportDomains)
            MySupportDomains.Add(d);
        EnableAutoSync = _config.EnableAutoSync;

        var idx = Array.IndexOf(ProxyTypes, _config.ProxyType);
        SelectedProxyIndex = idx < 0 ? 1 : idx;

        AddAddressCommand = new RelayCommand(_ => AddAddress());
        RemoveAddressCommand = new RelayCommand(p =>
        {
            if (p is string s)
            {
                MonitoredAddresses.Remove(s);
                OnPropertyChanged(nameof(IsMonitoredEmpty));
            }
        });

        AddSupportDomainCommand = new RelayCommand(_ => AddSupportDomain());
        RemoveSupportDomainCommand = new RelayCommand(p =>
        {
            if (p is string s) MySupportDomains.Remove(s);
        });

        foreach (var kv in _config.DomainEnterpriseMappings)
            DomainMappings.Add($"{kv.Key} → {kv.Value}");
        foreach (var kv in _config.ProductAliases)
            ProductAliasItems.Add($"{kv.Key} → {kv.Value}");

        AddMappingCommand = new RelayCommand(_ => AddMapping());
        RemoveMappingCommand = new RelayCommand(p =>
        {
            if (p is string s) DomainMappings.Remove(s);
        });

        AddAliasCommand = new RelayCommand(_ => AddAlias());
        RemoveAliasCommand = new RelayCommand(p =>
        {
            if (p is string s) ProductAliasItems.Remove(s);
        });
    }

    private void AddAddress()
    {
        var a = NewAddress.Trim();
        if (a.Length == 0) return;
        if (!MonitoredAddresses.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
        {
            MonitoredAddresses.Add(a);
            OnPropertyChanged(nameof(IsMonitoredEmpty));
        }
        NewAddress = "";
    }

    private void AddSupportDomain()
    {
        var d = NewSupportDomain.Trim().ToLowerInvariant();
        if (d.Length == 0) return;
        if (!MySupportDomains.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase)))
            MySupportDomains.Add(d);
        NewSupportDomain = "";
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

    /// <summary>添加 产品简称→全称 映射（key 不区分大小写）。</summary>
    private void AddAlias()
    {
        var sh = NewAliasShort.Trim();
        var full = NewAliasFull.Trim();
        if (sh.Length == 0 || full.Length == 0) return;
        if (!ProductAliasItems.Any(x => x.StartsWith(sh + " → ", StringComparison.OrdinalIgnoreCase)))
            ProductAliasItems.Add($"{sh} → {full}");
        NewAliasShort = "";
        NewAliasFull = "";
    }

    public void Save()
    {
        _config.MonitoredAddresses = MonitoredAddresses.ToList();
        _config.MySupportDomains = MySupportDomains.ToList();
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
        var aliasDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ProductAliasItems)
        {
            var idx = entry.IndexOf(" → ");
            if (idx < 0) continue;
            var sh = entry[..idx].Trim();
            var full = entry[(idx + 3)..].Trim();
            if (sh.Length > 0 && full.Length > 0) aliasDict[sh] = full;
        }
        _config.ProductAliases = aliasDict;
        if (SelectedProxyIndex >= 0 && SelectedProxyIndex < ProxyTypes.Length)
            _config.ProxyType = ProxyTypes[SelectedProxyIndex];
        _config.EnableAutoSync = EnableAutoSync;
        _workflow.SaveConfig(_config);
    }
}
