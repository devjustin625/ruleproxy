using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RuleProxy.Native.Core;

namespace RuleProxy.Native;

/// <summary>应用入口：单实例互斥锁，支持 --minimized 启动参数。</summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\RuleProxy_SingleInstance";
    private const string WakeEventName = @"Local\RuleProxy_WakeExisting";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _wakeEvent;
    private DispatcherTimer? _wakeTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        if (TryApplyUpdate(e.Args))
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            WakeExisting();
            Shutdown();
            return;
        }
        _ownsMutex = true;

        base.OnStartup(e);
    _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        var startMinimized = e.Args.Any(arg => arg is "--minimized" or "-m");
        var window = new MainWindow(startMinimized);
        MainWindow = window;
    _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _wakeTimer.Tick += (_, _) => ProcessWakeRequest();
    _wakeTimer.Start();
        window.Show();
        if (startMinimized)
        {
            window.HideToTray(showNotification: false);
        }
    }

    private static bool TryApplyUpdate(string[] args)
    {
        if (args.Length is < 4 or > 5 || args[0] != "--apply-update" ||
            !int.TryParse(args[1], out var parentPid) ||
            (args.Length == 5 && args[4] != "--minimized"))
        {
            return false;
        }

        UpdateService.ApplyUpdate(parentPid, args[2], args[3], args.Length == 5);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeTimer?.Stop();
        _wakeEvent?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void ProcessWakeRequest()
    {
        if (_wakeEvent?.WaitOne(0) == true && MainWindow is MainWindow window)
        {
            window.ShowFromTray();
        }
    }

    private static void WakeExisting()
    {
        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out _);
        wakeEvent.Set();
    }
}

