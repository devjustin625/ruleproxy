"""RuleProxy 应用入口。"""
from __future__ import annotations

import sys

import ctypes
import sys

from PyQt6.QtGui import QIcon
from PyQt6.QtWidgets import QApplication, QMessageBox, QSystemTrayIcon

from .core.config import APP_NAME, APP_VERSION, load_config
from .gui.main_window import MainWindow
from .resources import resource_path

_WINDOW_TITLE = f"{APP_NAME} — 分应用 / 分端口代理"
_MUTEX = None  # 持有互斥句柄，保证单实例


def _wake_existing() -> bool:
    """把已运行实例的主窗口调到前台（最小化到托盘时也会恢复显示）。"""
    try:
        user32 = ctypes.windll.user32
        hwnd = user32.FindWindowW(None, _WINDOW_TITLE)
        if hwnd:
            user32.ShowWindow(hwnd, 9)   # SW_RESTORE
            user32.SetForegroundWindow(hwnd)
            return True
    except Exception:
        pass
    return False


def _ensure_single_instance() -> bool:
    """Windows 单实例：已运行时唤醒旧窗口并返回 False。"""
    global _MUTEX
    if sys.platform != "win32":
        return True
    try:
        _MUTEX = ctypes.windll.kernel32.CreateMutexW(None, False, "Local\\RuleProxy_SingleInstance")
        if ctypes.windll.kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
            return False
    except Exception:
        pass
    return True


def main() -> int:
    if not _ensure_single_instance():
        # 已有实例在运行：尝试唤醒其窗口；若失败则提示后退出
        app = QApplication(sys.argv)
        if not _wake_existing():
            QMessageBox.information(None, APP_NAME, "RuleProxy 已在运行。")
        return 0

    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    app.setApplicationVersion(APP_VERSION)
    app.setStyle("Fusion")
    # 关闭/隐藏主窗口时保持托盘常驻，不退出应用
    app.setQuitOnLastWindowClosed(False)
    # 窗口 / 任务栏图标（源码与打包环境均可用）
    icon = QIcon(resource_path("icon.ico"))
    app.setWindowIcon(icon)
    cfg = load_config()
    window = MainWindow(cfg)
    window.setWindowIcon(icon)
    # 启动最小化：命令行 --minimized（开机自启动用）或设置里的选项
    start_minimized = ("--minimized" in sys.argv) or cfg.start_minimized
    if start_minimized and QSystemTrayIcon.isSystemTrayAvailable():
        window.start_in_tray()
    else:
        window.show()
    return app.exec()
