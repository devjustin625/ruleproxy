using System.IO;
using Microsoft.Win32;

namespace RuleProxy.Native.Core;

/// <summary>开机自启动：通过 HKCU Run 注册表键实现。
/// 每次启用时始终使用当前 exe 实际路径，支持任意目录运行。
/// 可按设置附加 --minimized 参数，让程序开机后最小化到托盘。</summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RuleProxy";

    public static bool IsEnabled
    {
        get
        {
            var value = Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(value)) return false;
            var exePath = ExtractExePath(value);
            return exePath is not null && File.Exists(exePath);
        }
    }

    /// <summary>注册表中记录的自启动 exe 路径，可能已不存在。</summary>
    public static string? RegistryExePath
    {
        get
        {
            var value = Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue(ValueName) as string;
            return value is not null ? ExtractExePath(value) : null;
        }
    }

    public static void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return;
            }
            var arguments = startMinimized ? " --minimized" : "";
            key.SetValue(ValueName, $"\"{exePath}\"{arguments}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    /// <summary>如果当前 exe 路径与注册表记录不一致，更新为当前路径。
    /// 用户把 exe 移动到其他目录后运行，调用此方法可自动修正自启动指向。</summary>
    public static void SyncToCurrentPath(bool startMinimized)
    {
        if (!IsEnabled) return;
        var currentExe = Environment.ProcessPath;
        var registered = RegistryExePath;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe)) return;
        if (string.Equals(currentExe, registered, StringComparison.OrdinalIgnoreCase)) return;
        // 路径变了，更新
        SetEnabled(true, startMinimized);
    }

    /// <summary>从注册表值中提取 exe 路径（去掉引号和参数）。</summary>
    private static string? ExtractExePath(string registryValue)
    {
        var trimmed = registryValue.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
