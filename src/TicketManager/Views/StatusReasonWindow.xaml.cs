using System.Windows;

namespace TicketManager.Views;

/// <summary>手工设置线程状态时输入理由的小窗口。</summary>
public partial class StatusReasonWindow : Window
{
    /// <summary>用户输入的理由（确定后有效，可留空）。</summary>
    public string Reason { get; private set; } = "";

    public StatusReasonWindow(string status, string currentReason = "", string aiSummary = "")
    {
        InitializeComponent();
        StatusLabel.Text = $"状态：{status}";
        // 已有手工理由时优先保留；否则用 AI 归纳意见作为默认内容（可修改/清空）
        ReasonBox.Text = string.IsNullOrWhiteSpace(currentReason) ? aiSummary : currentReason;
        AiHint.Visibility = !string.IsNullOrWhiteSpace(aiSummary) && string.IsNullOrWhiteSpace(currentReason)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReasonBox.Focus();
        ReasonBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Reason = ReasonBox.Text.Trim();
        DialogResult = true;
    }
}
