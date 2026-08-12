using Microsoft.Win32;

namespace RuleProxy.Native.Core;

/// <summary>开机自启动：通过 HKCU Run 注册表键实现，启动参数 --minimized 让程序开机后最小化到托盘。</summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RuleProxy";

    public static bool IsEnabled =>
        Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue(ValueName) is not null;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }
            key.SetValue(ValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
