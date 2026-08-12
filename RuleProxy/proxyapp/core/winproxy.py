"""Windows 系统代理设置（WinINet）。

说明：WinINet 系统代理只对遵循系统代理的应用（多数浏览器等）生效；
对不遵循系统代理的应用，请在应用内手动填写本工具的代理地址。
"""
from __future__ import annotations

import sys

PROXY_SETTING = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings"

# wininet 常量
INTERNET_OPTION_SETTINGS_CHANGED = 39
INTERNET_OPTION_REFRESH = 37


def _is_windows() -> bool:
    return sys.platform == "win32"


def set_system_proxy(host: str, port: int) -> bool:
    if not _is_windows():
        return False
    try:
        import winreg
    except ImportError:
        return False
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, PROXY_SETTING, 0, winreg.KEY_SET_VALUE) as key:
            winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 1)
            winreg.SetValueEx(key, "ProxyServer", 0, winreg.REG_SZ, f"{host}:{port}")
            winreg.SetValueEx(key, "ProxyOverride", 0, winreg.REG_SZ, "<local>")
        _notify_internet_settings()
        return True
    except Exception:
        return False


def clear_system_proxy() -> bool:
    if not _is_windows():
        return False
    try:
        import winreg
    except ImportError:
        return False
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, PROXY_SETTING, 0, winreg.KEY_SET_VALUE) as key:
            winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 0)
        _notify_internet_settings()
        return True
    except Exception:
        return False


def is_system_proxy_on() -> bool:
    if not _is_windows():
        return False
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, PROXY_SETTING, 0, winreg.KEY_READ) as key:
            value, _ = winreg.QueryValueEx(key, "ProxyEnable")
            return bool(value)
    except Exception:
        return False


def _notify_internet_settings() -> None:
    try:
        import ctypes
        ctypes.windll.wininet.InternetSetOptionW(None, INTERNET_OPTION_SETTINGS_CHANGED, None, 0)
        ctypes.windll.wininet.InternetSetOptionW(None, INTERNET_OPTION_REFRESH, None, 0)
    except Exception:
        pass
