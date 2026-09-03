using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TicketManager.Models;
using TicketManager.Services;
using TicketManager.ViewModels;

namespace TicketManager.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    /// <summary>设置窗口当前是否处于打开状态（避免配置检查在设置窗口弹出期间重复提示）。</summary>
    public static bool IsOpen { get; private set; }

    public SettingsWindow(WorkflowService workflow)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(workflow);
        DataContext = _vm;
        DeepSeekKeyBox.Password = _vm.Config.DeepSeekApiKey;
        ImapPasswordBox.Password = _vm.Config.ImapPassword;
        SmtpPasswordBox.Password = _vm.Config.SmtpPassword;
        ZohoClientIdBox.Password = _vm.Config.ZohoClientId;
        ZohoClientSecretBox.Password = _vm.Config.ZohoClientSecret;
        ZohoRefreshTokenBox.Password = _vm.Config.ZohoRefreshToken;
        ReorderTabs(); // 把最常配置的 Zoho REST / DeepSeek 置顶，方便优先完成关键配置
        IsOpen = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        IsOpen = false;
        base.OnClosed(e);
    }

    /// <summary>把「同步设置」「Zoho REST API」「IMAP」「SMTP 发送」四个同步/发送相关设置页移到最前（依次第 1/2/3/4）。</summary>
    private void ReorderTabs()
    {
        var sync = FindTab("同步设置");
        var zoho = FindTab("Zoho REST API");
        var imap = FindTab("IMAP");
        var smtp = FindTab("SMTP 发送");
        if (sync != null) { MainTab.Items.Remove(sync); MainTab.Items.Insert(0, sync); }
        if (zoho != null) { MainTab.Items.Remove(zoho); MainTab.Items.Insert(1, zoho); }
        if (imap != null) { MainTab.Items.Remove(imap); MainTab.Items.Insert(2, imap); }
        if (smtp != null) { MainTab.Items.Remove(smtp); MainTab.Items.Insert(3, smtp); }
    }

    private TabItem? FindTab(string header)
    {
        foreach (var item in MainTab.Items)
            if (item is TabItem t && string.Equals((string)t.Header, header, StringComparison.Ordinal))
                return t;
        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Config.DeepSeekApiKey = DeepSeekKeyBox.Password;
        _vm.Config.ImapPassword = ImapPasswordBox.Password;
        _vm.Config.SmtpPassword = SmtpPasswordBox.Password;
        _vm.Config.ZohoClientId = ZohoClientIdBox.Password;
        _vm.Config.ZohoClientSecret = ZohoClientSecretBox.Password;
        _vm.Config.ZohoRefreshToken = ZohoRefreshTokenBox.Password;
        _vm.Save();
        DialogResult = true;
    }

    /// <summary>把 Zoho Scope 复制到剪贴板（一键复制）。</summary>
    private void CopyScope_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("ZohoMail.accounts.READ,ZohoMail.folders.READ,ZohoMail.messages.READ,ZohoMail.messages.CREATE");
            MessageBox.Show(this, "Scope 已复制到剪贴板，可直接粘贴到 Zoho 的 Generate Token 页面。", "复制成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", "复制", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>点击蓝色超链接：用系统默认浏览器打开目标网页。</summary>
    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开链接：{ex.Message}", "打开链接",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>测试当前代理能否连通外网（按所选代理类型经代理 TCP 连接到目标主机）。</summary>
    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config;
        if (!cfg.UseProxy || string.IsNullOrWhiteSpace(cfg.ProxyHost))
        {
            ProxyTestText.Text = "请先勾选「启用代理」并填写 地址/端口。";
            return;
        }
        MailKit.Net.Proxy.IProxyClient? proxy = cfg.ProxyType switch
        {
            "Socks4" => new MailKit.Net.Proxy.Socks4Client(cfg.ProxyHost, cfg.ProxyPort),
            "Socks5" => new MailKit.Net.Proxy.Socks5Client(cfg.ProxyHost, cfg.ProxyPort),
            "Http" => new MailKit.Net.Proxy.HttpProxyClient(cfg.ProxyHost, cfg.ProxyPort),
            _ => null
        };
        if (proxy == null)
        {
            ProxyTestText.Text = "无法识别的代理类型。";
            return;
        }
        const string targetHost = "www.gstatic.com";
        const int targetPort = 443;
        ProxyTestText.Text = $"正在测试代理 {cfg.ProxyType} {cfg.ProxyHost}:{cfg.ProxyPort} …";
        try
        {
            using var socket = await proxy.ConnectAsync(targetHost, targetPort);
            ProxyTestText.Text = $"✅ 代理连通正常：{cfg.ProxyType} {cfg.ProxyHost}:{cfg.ProxyPort} → {targetHost}:{targetPort}";
        }
        catch (Exception ex)
        {
            ProxyTestText.Text = $"❌ 代理连接失败：{ex.Message}";
        }
    }

    /// <summary>用当前输入框（可能未保存）的 SMTP 凭据测试连接。</summary>
    private async void TestSmtp_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config;
        if (string.IsNullOrWhiteSpace(cfg.SmtpHost))
        {
            SmtpTestText.Text = "请先填写 SMTP 服务器地址。";
            return;
        }
        var test = new AppConfig
        {
            SmtpHost = cfg.SmtpHost,
            SmtpPort = cfg.SmtpPort,
            SmtpUseSsl = cfg.SmtpUseSsl,
            SmtpUsername = cfg.SmtpUsername,
            SmtpPassword = SmtpPasswordBox.Password,
            // SMTP 账号/密码留空时回退用 IMAP 凭据（与真实发送逻辑一致），测试时带上 IMAP 凭据兜底
            ImapUsername = cfg.ImapUsername,
            ImapPassword = ImapPasswordBox.Password,
            // 带上代理设置：SMTP 走代理（如开启）
            UseProxy = cfg.UseProxy,
            ProxyType = cfg.ProxyType,
            ProxyHost = cfg.ProxyHost,
            ProxyPort = cfg.ProxyPort,
            ProxyForSmtp = cfg.ProxyForSmtp
        };
        SmtpTestText.Text = "正在测试…";
        var (_, msg) = await new Services.SmtpSendService(test).TestAsync();
        SmtpTestText.Text = msg;
    }

    /// <summary>用当前输入框（可能未保存）的 IMAP 凭据测试连接。</summary>
    private async void TestImap_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config;
        var test = new AppConfig
        {
            ImapHost = cfg.ImapHost,
            ImapPort = cfg.ImapPort,
            ImapUseSsl = cfg.ImapUseSsl,
            ImapUsername = cfg.ImapUsername,
            ImapPassword = ImapPasswordBox.Password,
            ImapFolder = cfg.ImapFolder,
            ImapSentFolder = cfg.ImapSentFolder,
            // 带上代理设置：IMAP 走代理（如开启）
            UseProxy = cfg.UseProxy,
            ProxyType = cfg.ProxyType,
            ProxyHost = cfg.ProxyHost,
            ProxyPort = cfg.ProxyPort,
            ProxyForImap = cfg.ProxyForImap
        };
        ImapTestText.Text = "正在测试…";
        var (_, msg) = await new Services.ImapSyncService(test).TestConnectionAsync();
        ImapTestText.Text = msg;
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
