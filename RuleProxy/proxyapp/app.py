"""RuleProxy 应用入口。"""
from __future__ import annotations

import sys

from PyQt6.QtGui import QIcon
from PyQt6.QtWidgets import QApplication, QSystemTrayIcon

from .core.config import APP_NAME, APP_VERSION, load_config
from .gui.main_window import MainWindow
from .resources import resource_path


def main() -> int:
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
