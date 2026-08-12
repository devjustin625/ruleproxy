using System.Runtime.InteropServices;
using System.Windows;

namespace RuleProxy.Native;

/// <summary>应用入口：单实例互斥锁，支持 --minimized 启动参数。</summary>
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = @"Local\RuleProxy_SingleInstance";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            WakeExisting();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var startMinimized = e.Args.Any(arg => arg is "--minimized" or "-m");
        var window = new MainWindow(startMinimized);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void WakeExisting()
    {
        var hwnd = FindWindowW(null, "RuleProxy — 分应用 / 分端口代理");
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

