"""Windows 开机自启动（注册表 Run 键，无需管理员权限）。

开机启动时带 --minimized 参数，直接最小化到托盘运行。
"""
from __future__ import annotations

import os
import sys

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
APP_NAME = "RuleProxy"


def _command() -> str:
    """构造自启动命令行：打包后指向 exe，源码运行指向 python + run.py。"""
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}" --minimized'
    # autostart.py 位于 proxyapp/core/，项目根在其上三级
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    run_py = os.path.join(root, "run.py")
    return f'"{sys.executable}" "{run_py}" --minimized'


def is_enabled() -> bool:
    if sys.platform != "win32":
        return False
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_READ) as key:
            winreg.QueryValueEx(key, APP_NAME)
        return True
    except OSError:
        return False


def enable() -> bool:
    if sys.platform != "win32":
        return False
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
            winreg.SetValueEx(key, APP_NAME, 0, winreg.REG_SZ, _command())
        return True
    except Exception:
        return False


def disable() -> bool:
    if sys.platform != "win32":
        return False
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
            winreg.DeleteValue(key, APP_NAME)
        return True
    except FileNotFoundError:
        return True
    except Exception:
        return False
