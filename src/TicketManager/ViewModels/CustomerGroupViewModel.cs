using System.Collections.ObjectModel;

namespace TicketManager.ViewModels;

public class CustomerGroupViewModel
{
    public string Name { get; }
    public ObservableCollection<ProductGroupViewModel> Products { get; } = new();

    public CustomerGroupViewModel(string name) => Name = name;
    public string CountText => $"{Products.Sum(p => p.Threads.Count)} 条";
}
