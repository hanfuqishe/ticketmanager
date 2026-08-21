using System.Collections.ObjectModel;

namespace TicketManager.ViewModels;

public class ProductGroupViewModel
{
    public string Name { get; }
    public ObservableCollection<EmailNodeViewModel> Threads { get; } = new();

    public ProductGroupViewModel(string name) => Name = name;
    public string CountText => $"{Threads.Count} 条";

    /// <summary>默认展开（按“展开层次”设置，显示产品下的邮件树根）。</summary>
    public bool ExpandedByDefault { get; set; } = true;
}
