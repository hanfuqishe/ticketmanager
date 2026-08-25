using System.Windows;

namespace TicketManager.Views;

/// <summary>手工输入产品/客户名称的小窗口（候选列表里没有时使用）。</summary>
public partial class MetaInputWindow : Window
{
    /// <summary>用户输入的名称（确定后有效，去首尾空白）。</summary>
    public string InputName { get; private set; } = "";

    public MetaInputWindow(bool isProduct)
    {
        InitializeComponent();
        Title = isProduct ? "设置产品 — 手工输入" : "设置客户 — 手工输入";
        PromptText.Text = isProduct ? "输入要设置的产品名称：" : "输入要设置的客户名称：";
        NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InputName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
