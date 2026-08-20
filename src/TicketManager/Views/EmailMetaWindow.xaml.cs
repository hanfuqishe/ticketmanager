using System.Windows;
using TicketManager.Services;

namespace TicketManager.Views;

public partial class EmailMetaWindow : Window
{
    public string Product { get; private set; } = "";
    public string Enterprise { get; private set; } = "";

    public EmailMetaWindow(WorkflowService workflow, Models.EmailMessage email)
    {
        InitializeComponent();
        // 产品候选列表 = 主题方括号标注过的产品；当前值不在列表中时也显示出来（仅显示当前值，不新增候选）
        var products = workflow.GetKnownProducts().ToList();
        if (!string.IsNullOrEmpty(email.Product) &&
            !products.Contains(email.Product, StringComparer.OrdinalIgnoreCase))
            products.Insert(0, email.Product);
        ProductBox.ItemsSource = products;
        ProductBox.SelectedItem = email.Product;

        // 客户企业候选列表同样只可选择不可输入；当前值不在列表中时显示出来（仅显示，不新增候选）
        var enterprises = workflow.GetKnownEnterprises().ToList();
        if (!string.IsNullOrEmpty(email.Enterprise) &&
            !enterprises.Contains(email.Enterprise, StringComparer.OrdinalIgnoreCase))
            enterprises.Insert(0, email.Enterprise);
        EnterpriseBox.ItemsSource = enterprises;
        EnterpriseBox.SelectedItem = email.Enterprise;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Product = ProductBox.Text.Trim();
        Enterprise = EnterpriseBox.Text.Trim();
        DialogResult = true;
    }
}
