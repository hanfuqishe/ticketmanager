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

    // ===== 日志写入策略 =====
    // 单文件上限：超过即轮转（error.log → error.1.log → error.2.log，最旧的删除）。
    // 不加限制时全局异常处理器会把日志写到 GB 级（实测 2026-08-18 一天写过 13 万条 XamlParseException，
    // 日志膨胀到 744MB / 900 万行）。
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private const int MaxLogFiles = 3; // error.log + error.1.log + error.2.log

    /// <summary>同源同异常的去重窗口：窗口内重复出现只累计次数，不再逐条写盘（防止异常风暴写爆日志）。</summary>
    private static readonly TimeSpan LogDedupWindow = TimeSpan.FromSeconds(30);
    private static readonly object LogLock = new();
    private static readonly Dictionary<string, (DateTime LastAt, int Suppressed)> LogDedup = new();

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

        ArchiveOversizedLog(); // 历史遗留的超大日志先归档，避免继续往同一个文件追加

        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));
        DispatcherUnhandledException += (_, args) =>
        {
            // 记录后继续运行（不让单个 UI 异常杀掉进程）；异常风暴由 Log 的限流保护
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

    /// <summary>
    /// 写诊断日志（%AppData%\TicketManager\error.log）。
    /// 带单文件大小轮转与同源异常限流：同一来源 + 同一异常首行的连续重复，30 秒内只落盘第一条，
    /// 之后的只计数，下次落盘时合并为“（同类异常已被抑制 N 次）”。
    /// 多线程调用安全（同步锁 + 追加写）。
    /// </summary>
    public static void Log(string source, Exception ex)
    {
        try
        {
            var now = DateTime.Now;
            var key = source + "|" + FirstLine(ex);
            string text;
            lock (LogLock)
            {
                var suppressed = 0;
                if (LogDedup.TryGetValue(key, out var prev))
                {
                    if (now - prev.LastAt < LogDedupWindow)
                    {
                        LogDedup[key] = (prev.LastAt, prev.Suppressed + 1); // 窗口内重复 → 只计数
                        return;
                    }
                    suppressed = prev.Suppressed;
                }
                LogDedup[key] = (now, 0);
                if (LogDedup.Count > 500) PruneLogDedup(now); // 键含异常文本，防止字典无限增长

                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RotateLogIfNeeded();
                var head = $"[{now:yyyy-MM-dd HH:mm:ss}] [{source}] {FirstLine(ex)}";
                text = suppressed > 0
                    ? $"{head}（同类异常已被抑制 {suppressed} 次）\n{ex}\n\n"
                    : $"{head}\n{ex}\n\n";
                File.AppendAllText(LogPath, text);
            }
        }
        catch
        {
            // 写日志本身失败时忽略（不能再写日志，否则递归）
        }
    }

    /// <summary>异常首行（类型 + 消息，截断），用于日志限流的去重键与标题。</summary>
    private static string FirstLine(Exception ex)
    {
        var msg = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        if (msg.Length > 120) msg = msg[..120] + "…";
        return $"{ex.GetType().Name}: {msg}";
    }

    /// <summary>清掉限流表里已过窗口的条目（调用方需持有 LogLock）。</summary>
    private static void PruneLogDedup(DateTime now)
    {
        var stale = LogDedup.Where(kv => now - kv.Value.LastAt >= LogDedupWindow)
                            .Select(kv => kv.Key).ToList();
        foreach (var k in stale) LogDedup.Remove(k);
    }

    /// <summary>日志超过单文件上限时轮转：error.2.log 删除，error.1.log → error.2.log，error.log → error.1.log（调用方需持有 LogLock）。</summary>
    private static void RotateLogIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < MaxLogBytes) return;
        var dir = Path.GetDirectoryName(LogPath)!;
        for (int i = MaxLogFiles - 1; i >= 1; i--)
        {
            var older = Path.Combine(dir, $"error.{i}.log");
            var newer = i == 1 ? LogPath : Path.Combine(dir, $"error.{i - 1}.log");
            if (File.Exists(older)) File.Delete(older);
            if (File.Exists(newer)) File.Move(newer, older, overwrite: true);
        }
    }

    /// <summary>启动时把超过上限的旧日志改名为 error.old.log（保留供排查，之后由轮转自然淘汰），使本次运行从空日志开始。</summary>
    private static void ArchiveOversizedLog()
    {
        try
        {
            var file = new FileInfo(LogPath);
            if (!file.Exists || file.Length < MaxLogBytes) return;
            var archive = Path.Combine(Path.GetDirectoryName(LogPath)!, "error.old.log");
            if (File.Exists(archive)) File.Delete(archive);
            File.Move(LogPath, archive);
        }
        catch { /* 归档失败不影响启动 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Db?.Dispose();
        base.OnExit(e);
    }
}
