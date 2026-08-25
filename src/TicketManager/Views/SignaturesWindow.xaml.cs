using System.Collections.ObjectModel;
using System.Windows;
using TicketManager.Models;
using TicketManager.Services;

namespace TicketManager.Views;

/// <summary>签名管理窗口：维护多个不同名称的签名（字体/字号/颜色统一用“设置→字体…”里的全局邮件字体设置）。</summary>
public partial class SignaturesWindow : Window
{
    private readonly WorkflowService _workflow;

    public ObservableCollection<EmailSignature> Signatures { get; } = new();

    public SignaturesWindow(WorkflowService workflow)
    {
        InitializeComponent();
        _workflow = workflow;
        DataContext = this;
        foreach (var s in workflow.LoadSignatures()) Signatures.Add(s);
        if (Signatures.Count > 0) SignatureList.SelectedIndex = 0;
        if (Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var sig = new EmailSignature { Name = $"签名 {Signatures.Count + 1}" };
        Signatures.Add(sig);
        SignatureList.SelectedItem = sig;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SignatureList.SelectedItem is EmailSignature sig) Signatures.Remove(sig);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _workflow.SaveSignatures(Signatures.ToList());
        DialogResult = true;
    }
}
