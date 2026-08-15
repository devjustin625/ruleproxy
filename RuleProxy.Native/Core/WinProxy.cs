using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RuleProxy.Native.Core;

/// <summary>Windows 系统代理（WinINet）开关，通过注册表 + 刷新通知实现。</summary>
public static class WinProxy
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static bool IsEnabled =>
        (int?)(Registry.CurrentUser.OpenSubKey(InternetSettings)?.GetValue("ProxyEnable")) == 1;

    public static void SetProxy(string host, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        Refresh();
    }

    public static void ClearProxy()
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Refresh();
    }

    /// <summary>读取当前系统代理地址（如 "127.0.0.1:8888"），未启用时返回 null。</summary>
    public static string? GetProxyServer() =>
        Registry.CurrentUser.OpenSubKey(InternetSettings)?.GetValue("ProxyServer") as string;

    /// <summary>判断当前系统代理是否正指向给定监听地址（用于退出时只清除本程序设置的代理）。</summary>
    public static bool IsSetTo(string host, int port)
    {
        return IsProxyServerSetTo(IsEnabled, GetProxyServer(), host, port);
    }

    /// <summary>判断读取到的系统代理配置是否启用且包含给定的代理终结点。</summary>
    public static bool IsProxyServerSetTo(bool enabled, string? proxyServer, string host, int port)
    {
        if (!enabled || string.IsNullOrWhiteSpace(proxyServer))
        {
            return false;
        }

        var endpoint = $"{host}:{port}";
        return proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Contains('=') ? value[(value.IndexOf('=') + 1)..] : value)
            .Any(value => string.Equals(value, endpoint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>通知 WinINet 重新读取系统代理设置。</summary>
    public static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
