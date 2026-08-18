using System.IO;
using System.Windows;
using System.Windows.Threading;
using TicketManager.Services;

namespace TicketManager;

public partial class App : Application
{
    public static DatabaseService Db { get; private set; } = default!;
    public static WorkflowService Workflow { get; private set; } = default!;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TicketManager", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));
        DispatcherUnhandledException += (_, args) =>
        {
            Log("Dispatcher", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Task", args.Exception);
            args.SetObserved();
        };

        try
        {
            Db = new DatabaseService();
            Db.Initialize();
            Workflow = new WorkflowService(Db);
        }
        catch (Exception ex)
        {
            Log("Startup", ex);
            throw;
        }
    }

    public static void Log(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n");
        }
        catch
        {
            // 日志写入失败时忽略
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Db?.Dispose();
        base.OnExit(e);
    }
}
