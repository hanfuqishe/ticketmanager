using TicketManager.Services;
using TicketManager.ViewModels;
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(dbPath);
db.Initialize();
var wf = new WorkflowService(db);
wf.LoadConfig();
var email = db.LoadAllEmails().FirstOrDefault(e => e.Subject.Contains("[## 13801919 ##]") && e.Folder == "Inbox");
if (email == null) { Console.WriteLine("未找到测试邮件"); return; }
var vm = new ReplyTicketViewModel(wf, email);
Console.WriteLine("标题: " + vm.Subject);
Console.WriteLine("回复对象候选:");
foreach (var r in vm.Recipients) Console.WriteLine("  " + r.Label + (r == vm.SelectedRecipient ? "  ←默认" : ""));
Console.WriteLine("默认收信人: " + vm.RecipientEmail);
Console.WriteLine("抄送(默认): " + vm.CcDisplay);
// 切到客户接口人
var customer = vm.Recipients.FirstOrDefault(r => r.Role == "客户接口人");
if (customer != null) { vm.SelectedRecipient = customer; Console.WriteLine("切到 " + customer.Label + " → 收信人=" + vm.RecipientEmail + " 抄送=" + vm.CcDisplay); }
