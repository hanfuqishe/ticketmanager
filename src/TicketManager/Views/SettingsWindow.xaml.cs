using System.Windows;
using TicketManager.Models;
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
        ZohoClientIdBox.Password = _vm.Config.ZohoClientId;
        ZohoClientSecretBox.Password = _vm.Config.ZohoClientSecret;
        ZohoRefreshTokenBox.Password = _vm.Config.ZohoRefreshToken;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Config.ImapPassword = ImapPasswordBox.Password;
        _vm.Config.DeepSeekApiKey = DeepSeekKeyBox.Password;
        _vm.Config.ZohoClientId = ZohoClientIdBox.Password;
        _vm.Config.ZohoClientSecret = ZohoClientSecretBox.Password;
        _vm.Config.ZohoRefreshToken = ZohoRefreshTokenBox.Password;
        _vm.Save();
        DialogResult = true;
    }

    /// <summary>用当前输入框（可能未保存）的凭据测试 Zoho REST API 连接。</summary>
    private async void TestZoho_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config;
        var test = new AppConfig
        {
            ZohoApiBase = cfg.ZohoApiBase,
            ZohoClientId = ZohoClientIdBox.Password,
            ZohoClientSecret = ZohoClientSecretBox.Password,
            ZohoRefreshToken = ZohoRefreshTokenBox.Password,
            ZohoAccountId = cfg.ZohoAccountId,
            // 带上代理设置：Zoho REST 走代理（直连不通的环境必须）
            UseProxy = cfg.UseProxy,
            ProxyType = cfg.ProxyType,
            ProxyHost = cfg.ProxyHost,
            ProxyPort = cfg.ProxyPort,
            ProxyForZoho = cfg.ProxyForZoho
        };
        var api = new Services.ZohoMailApiService(test);
        ZohoTestText.Text = "正在测试…";
        try
        {
            var accountId = await api.GetAccountIdAsync();
            if (accountId == null)
            {
                ZohoTestText.Text = "❌ 获取 Access Token 或账号失败：请检查 Client ID / Client Secret / Refresh Token。";
                return;
            }
            var folders = await api.GetFoldersAsync(accountId.Value);
            var inbox = folders.FirstOrDefault(f => f.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase));
            ZohoTestText.Text = $"✅ 连接成功：accountId={accountId}，文件夹 {folders.Count} 个" +
                                (inbox != null ? "（已识别 Inbox）" : "");
        }
        catch (Exception ex)
        {
            ZohoTestText.Text = $"❌ 测试失败：{ex.Message}";
        }
    }
}
