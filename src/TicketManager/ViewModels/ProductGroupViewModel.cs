using System.Collections.ObjectModel;

namespace TicketManager.ViewModels;

public class ProductGroupViewModel
{
    public string Name { get; }
    public ObservableCollection<ThreadViewModel> Threads { get; } = new();

    public ProductGroupViewModel(string name) => Name = name;
    public string CountText => $"{Threads.Count} 条";
}
