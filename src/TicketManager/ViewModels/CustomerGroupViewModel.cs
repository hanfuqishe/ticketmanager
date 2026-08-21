using System.Collections.ObjectModel;

namespace TicketManager.ViewModels;

public class CustomerGroupViewModel
{
    public string Name { get; }
    public ObservableCollection<ProductGroupViewModel> Products { get; } = new();

    public CustomerGroupViewModel(string name) => Name = name;
    public string CountText => $"{Products.Sum(p => p.Threads.Count)} 条";

    /// <summary>默认展开（按“展开层次”设置，显示客户下的产品）。</summary>
    public bool ExpandedByDefault { get; set; } = true;
}
