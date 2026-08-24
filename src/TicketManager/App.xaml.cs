using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using TicketManager.Services;

namespace TicketManager;

public partial class App : Application
{
    public static DatabaseService Db { get; private set; } = default!;
    public static WorkflowService Workflow { get; private set; } = default!;

    /// <summary>自定义窗口消息：第二个实例启动时发给已运行实例，请求把主窗口调到前台（含从托盘/最小化恢复）。</summary>
    public static int ShowMainMessage { get; private set; }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TicketManager", "error.log");

    // ===== 单实例互斥：保证同一用户只运行一个实例 =====
    private static Mutex? _singleInstance;
    private const string SingleInstanceMutexName = @"Local\TicketManager_SingleInstance";
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 注册“显示主窗口”自定义消息：第二个实例启动时发给已运行实例，让它在托盘/最小化时也能自行恢复
        ShowMainMessage = RegisterWindowMessage("TicketManager.ShowMainWindow");

        // 单实例检查：已有实例在运行 → 把它调到前台，退出本次启动
        _singleInstance = new Mutex(false, SingleInstanceMutexName);
        bool owns;
        try { owns = _singleInstance.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; } // 上一实例崩溃遗留的锁，视为可接管
        if (!owns)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

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

    /// <summary>通知已运行实例把主窗口调到前台（含从托盘/最小化恢复）。
    /// 窗口在托盘时已被 Hide，ShowWindow 无法恢复，必须发自定义消息由窗口自己 Show+Activate；消息不可用时退回 Win32 激活。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var hwnd = FindWindow(null, "工单邮件管理器");
            if (hwnd == IntPtr.Zero) return;
            if (ShowMainMessage != 0)
            {
                PostMessage(hwnd, ShowMainMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch { /* 前台切换失败不影响本次退出 */ }
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
