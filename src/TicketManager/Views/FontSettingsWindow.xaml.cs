using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TicketManager.Services;

namespace TicketManager.Views;

/// <summary>全局邮件字体设置窗口：字体/字号/颜色，统一应用于 邮件正文 与 签名。</summary>
public partial class FontSettingsWindow : Window
{
    private readonly WorkflowService _workflow;
    private readonly Models.AppConfig _config;

    public FontSettingsWindow(WorkflowService workflow)
    {
        InitializeComponent();
        _workflow = workflow;
        _config = workflow.Config;
        FontBox.ItemsSource = new[]
        {
            "Microsoft YaHei UI", "微软雅黑", "宋体", "黑体", "仿宋",
            "Arial", "Calibri", "Times New Roman", "Consolas"
        };
        FontBox.SelectedItem = _config.EmailFontFamily;
        SizeBox.ItemsSource = new List<double> { 9, 10, 11, 12, 14, 16, 18, 20 };
        SizeBox.SelectedItem = _config.EmailFontSize;
        ColorBox.Text = _config.EmailFontColor;
        UpdateColorPreview();
        if (Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (FontBox.SelectedItem is string fam && !string.IsNullOrWhiteSpace(fam))
            _config.EmailFontFamily = fam;
        if (SizeBox.SelectedItem is double sz && sz > 0)
            _config.EmailFontSize = sz;
        var hex = ColorBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(hex))
        {
            if (!hex.StartsWith("#")) hex = "#" + hex;
            try
            {
                ColorConverter.ConvertFromString(hex);
                _config.EmailFontColor = hex;
            }
            catch { /* 非法颜色保留原值 */ }
        }
        _workflow.SaveConfig(_config);
        DialogResult = true;
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateColorPreview();

    private void UpdateColorPreview()
    {
        try
        {
            var hex = ColorBox?.Text?.Trim() ?? "";
            if (hex.Length == 0)
            {
                ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
                return;
            }
            if (!hex.StartsWith("#")) hex = "#" + hex;
            ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { ColorPreview.Background = Brushes.Transparent; }
    }
}
