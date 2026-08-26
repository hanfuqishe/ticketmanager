using TicketManager.Services;
var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TicketManager", "ticketmanager.db");
var db = new DatabaseService(dbPath);
db.Initialize();
var wf = new WorkflowService(db);
wf.LoadConfig();
Console.WriteLine("domains = " + string.Join(";", wf.Config.MySupportDomains));
var cols = wf.FormatRecipients(wf.GetColleagueContacts());
Console.WriteLine("同事抄送候选:");
foreach (var c in cols) Console.WriteLine("  " + c);
