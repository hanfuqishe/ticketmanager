using System.Collections.ObjectModel;

namespace TicketManager.ViewModels;

/// <summary>
/// “已忽略的邮件”顶层大类：树最底部的独立分组，用专门图标标识，
/// 直接挂载所有被忽略的邮件（不虚构 客户/产品 分组）。
/// </summary>
public class IgnoredGroupViewModel
{
    public ObservableCollection<EmailNodeViewModel> Emails { get; } = new();

    public string Name => "已忽略的邮件";

    /// <summary>邮件数量文字（封）。</summary>
    public string CountText => $"{Emails.Count} 封";

    /// <summary>默认展开。</summary>
    public bool ExpandedByDefault { get; set; } = true;
}
