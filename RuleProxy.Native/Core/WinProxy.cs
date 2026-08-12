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
        NotifyChange();
    }

    public static void ClearProxy()
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyChange();
    }

    private static void NotifyChange()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
