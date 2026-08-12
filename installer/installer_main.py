"""RuleProxy 安装 / 卸载程序（Windows，无需管理员权限）。

用法：
  RuleProxy-Setup.exe                  安装（弹出安装对话框）
  RuleProxy-Setup.exe --uninstall      卸载（带确认）
  RuleProxy-Setup.exe --silent-install <目录>   静默安装（自动化/测试用）
  RuleProxy-Setup.exe --silent-uninstall        静默卸载（自动化/测试用）

安装时会把自身复制为 <安装目录>\\Uninstall.exe，并在控制面板注册卸载项。
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import time

APP_NAME = "RuleProxy"
APP_EXE = "RuleProxy.exe"
UNINSTALL_EXE = "Uninstall.exe"
VERSION = "0.1.0"
PUBLISHER = "RuleProxy"

UNINSTALL_ROOT = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
UNINSTALL_KEY = UNINSTALL_ROOT + "\\" + APP_NAME

APPDATA = os.environ.get("APPDATA", "")
USERPROFILE = os.environ.get("USERPROFILE", "")
LOCALAPPDATA = os.environ.get("LOCALAPPDATA", os.path.expanduser("~"))
DEFAULT_INSTALL_DIR = os.path.join(LOCALAPPDATA, "Programs", APP_NAME)
START_MENU_DIR = os.path.join(APPDATA, "Microsoft", "Windows", "Start Menu", "Programs", APP_NAME)
DESKTOP_DIR = os.path.join(USERPROFILE, "Desktop")


# ---------------------------------------------------------------- 工具
def _frozen_base() -> str:
    return getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))


def bundled_app_path() -> str:
    """安装程序内打包的应用 exe。

    - 打包后：位于 _MEIPASS/RuleProxy.exe
    - 源码调试：位于项目根 dist/RuleProxy.exe
    """
    if getattr(sys, "frozen", False):
        return os.path.join(_frozen_base(), APP_EXE)
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(root, "dist", APP_EXE)


def _taskkill() -> None:
    """结束可能正在运行的 RuleProxy 进程。"""
    try:
        subprocess.run(["taskkill", "/F", "/IM", APP_EXE], capture_output=True, check=False)
        time.sleep(0.3)
    except Exception:
        pass


def _create_shortcut(lnk_path: str, target: str, args: str = "", icon: str = "", workdir: str = "") -> None:
    """用 WScript.Shell 创建 .lnk 快捷方式（免第三方依赖）。"""
    try:
        os.makedirs(os.path.dirname(lnk_path), exist_ok=True)
    except OSError:
        pass
    ps = [
        "$ws = New-Object -ComObject WScript.Shell",
        f"$s = $ws.CreateShortcut({lnk_path!r})",
        f"$s.TargetPath = {target!r}",
    ]
    if args:
        ps.append(f"$s.Arguments = {args!r}")
    if icon:
        ps.append(f"$s.IconLocation = {icon!r}")
    if workdir:
        ps.append(f"$s.WorkingDirectory = {workdir!r}")
    ps.append("$s.Save()")
    try:
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", ";".join(ps)],
            capture_output=True, check=False,
        )
    except Exception:
        pass


def _installed_info():
    """从注册表读取已安装信息；未安装返回 None。"""
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY, 0, winreg.KEY_READ) as key:
            loc, _ = winreg.QueryValueEx(key, "InstallLocation")
        return {"install_dir": loc}
    except OSError:
        return None


# ---------------------------------------------------------------- 安装 / 卸载
def install(install_dir: str, desktop_shortcut: bool = True, launch_after: bool = True) -> bool:
    """执行安装，返回是否成功。"""
    import winreg
    try:
        os.makedirs(install_dir, exist_ok=True)
        _taskkill()

        # 1. 复制应用本体与卸载器
        app_src = bundled_app_path()
        if not os.path.exists(app_src):
            raise RuntimeError("安装程序内缺少应用文件")
        shutil.copy2(app_src, os.path.join(install_dir, APP_EXE))
        shutil.copy2(sys.executable, os.path.join(install_dir, UNINSTALL_EXE))

        app_path = os.path.join(install_dir, APP_EXE)
        icon_loc = f"{app_path},0"

        # 2. 开始菜单快捷方式（含卸载）
        _create_shortcut(os.path.join(START_MENU_DIR, f"{APP_NAME}.lnk"),
                         app_path, workdir=install_dir, icon=icon_loc)
        _create_shortcut(os.path.join(START_MENU_DIR, f"卸载 {APP_NAME}.lnk"),
                         os.path.join(install_dir, UNINSTALL_EXE), args="--uninstall",
                         icon=icon_loc, workdir=install_dir)

        # 3. 桌面快捷方式（可选）
        if desktop_shortcut:
            _create_shortcut(os.path.join(DESKTOP_DIR, f"{APP_NAME}.lnk"),
                             app_path, workdir=install_dir, icon=icon_loc)

        # 4. 控制面板卸载注册表项
        est_size = int(os.path.getsize(app_path) / 1024)
        uninst = f'"{os.path.join(install_dir, UNINSTALL_EXE)}" --uninstall'
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, UNINSTALL_ROOT, 0, winreg.KEY_SET_VALUE) as root_key:
            with winreg.CreateKeyEx(root_key, APP_NAME, 0, winreg.KEY_SET_VALUE) as key:
                winreg.SetValueEx(key, "DisplayName", 0, winreg.REG_SZ, f"{APP_NAME}（分应用/分端口代理）")
                winreg.SetValueEx(key, "DisplayVersion", 0, winreg.REG_SZ, VERSION)
                winreg.SetValueEx(key, "Publisher", 0, winreg.REG_SZ, PUBLISHER)
                winreg.SetValueEx(key, "DisplayIcon", 0, winreg.REG_SZ, icon_loc)
                winreg.SetValueEx(key, "InstallLocation", 0, winreg.REG_SZ, install_dir)
                winreg.SetValueEx(key, "UninstallString", 0, winreg.REG_SZ, uninst)
                winreg.SetValueEx(key, "QuietUninstallString", 0, winreg.REG_SZ, uninst)
                winreg.SetValueEx(key, "EstimatedSize", 0, winreg.REG_DWORD, est_size)
                winreg.SetValueEx(key, "NoModify", 0, winreg.REG_DWORD, 1)
                winreg.SetValueEx(key, "NoRepair", 0, winreg.REG_DWORD, 1)

        if launch_after:
            try:
                subprocess.Popen([app_path], cwd=install_dir)
            except Exception:
                pass
        return True
    except Exception as e:
        print(f"install error: {e}")
        return False


def uninstall() -> bool:
    """执行卸载，返回是否成功。"""
    import winreg
    info = _installed_info()
    install_dir = info["install_dir"] if info else None
    try:
        _taskkill()
        # 快捷方式
        try:
            shutil.rmtree(START_MENU_DIR)
        except OSError:
            pass
        try:
            os.remove(os.path.join(DESKTOP_DIR, f"{APP_NAME}.lnk"))
        except OSError:
            pass
        # 卸载注册表项
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, UNINSTALL_ROOT, 0, winreg.KEY_SET_VALUE) as root_key:
                winreg.DeleteKey(root_key, APP_NAME)
        except OSError:
            pass
        # 安装目录
        if install_dir and os.path.isdir(install_dir):
            try:
                shutil.rmtree(install_dir)
            except OSError:
                pass
        return True
    except Exception as e:
        print(f"uninstall error: {e}")
        return False


# ---------------------------------------------------------------- GUI
def _load_icon():
    from PyQt6.QtGui import QIcon
    ico = os.path.join(_frozen_base(), "icon.ico")
    return QIcon(ico) if os.path.exists(ico) else QIcon()


def run_install_gui() -> int:
    from PyQt6.QtWidgets import (QApplication, QCheckBox, QDialog, QFileDialog, QHBoxLayout,
                                 QLabel, QLineEdit, QMessageBox, QPushButton, QVBoxLayout)

    app = QApplication(sys.argv)
    app.setStyle("Fusion")
    icon = _load_icon()
    app.setWindowIcon(icon)

    existing = _installed_info()
    dialog = QDialog()
    dialog.setWindowTitle(f"安装 {APP_NAME}")
    dialog.setWindowIcon(icon)
    dialog.setMinimumWidth(480)
    lay = QVBoxLayout(dialog)

    title = QLabel(f"安装 {APP_NAME}（分应用 / 分端口代理）")
    title.setStyleSheet("font-size: 15px; font-weight: bold;")
    lay.addWidget(title)
    if existing:
        lay.addWidget(QLabel(f"检测到已安装：{existing['install_dir']}\n将覆盖更新。"))

    lay.addWidget(QLabel("安装位置："))
    path_row = QHBoxLayout()
    edit_path = QLineEdit(existing["install_dir"] if existing else DEFAULT_INSTALL_DIR)
    btn_browse = QPushButton("浏览…")
    path_row.addWidget(edit_path, 1)
    path_row.addWidget(btn_browse)
    lay.addLayout(path_row)

    def browse():
        p = QFileDialog.getExistingDirectory(dialog, "选择安装目录", edit_path.text())
        if p:
            edit_path.setText(p)

    btn_browse.clicked.connect(browse)

    chk_desktop = QCheckBox("创建桌面快捷方式")
    chk_desktop.setChecked(True)
    chk_launch = QCheckBox("安装完成后启动 RuleProxy")
    chk_launch.setChecked(True)
    lay.addWidget(chk_desktop)
    lay.addWidget(chk_launch)

    btn_row = QHBoxLayout()
    btn_cancel = QPushButton("取消")
    btn_install = QPushButton("安装")
    btn_install.setStyleSheet("background: #2f6feb; color: #ffffff; font-weight: bold; padding: 6px 18px;")
    btn_cancel.setStyleSheet("padding: 6px 18px;")
    btn_row.addStretch(1)
    btn_row.addWidget(btn_cancel)
    btn_row.addWidget(btn_install)
    lay.addLayout(btn_row)

    def do_install():
        target = edit_path.text().strip() or DEFAULT_INSTALL_DIR
        dialog.hide()
        ok = install(target, desktop_shortcut=chk_desktop.isChecked(), launch_after=chk_launch.isChecked())
        if ok:
            QMessageBox.information(dialog, "完成", f"安装完成！\n安装位置：{target}")
        else:
            QMessageBox.critical(dialog, "失败", "安装失败，请查看日志。")
        dialog.accept()

    btn_install.clicked.connect(do_install)
    btn_cancel.clicked.connect(dialog.reject)
    dialog.exec()
    return 0


def run_uninstall_gui() -> int:
    from PyQt6.QtWidgets import QApplication, QMessageBox

    app = QApplication(sys.argv)
    app.setWindowIcon(_load_icon())
    info = _installed_info()
    msg = "确定要卸载 RuleProxy 吗？" + (f"\n位置：{info['install_dir']}" if info else "") + "\n（配置保存在 ~/.ruleproxy，会保留）"
    if QMessageBox.question(None, "卸载 RuleProxy", msg,
                            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No) == QMessageBox.StandardButton.Yes:
        ok = uninstall()
        QMessageBox.information(None, "完成", "已卸载。" if ok else "卸载失败。")
    return 0


def main() -> int:
    args = sys.argv[1:]
    if "--uninstall" in args:
        return run_uninstall_gui()
    if "--silent-uninstall" in args:
        return 0 if uninstall() else 1
    if "--silent-install" in args:
        idx = args.index("--silent-install")
        target = args[idx + 1] if len(args) > idx + 1 else DEFAULT_INSTALL_DIR
        return 0 if install(target, desktop_shortcut=False, launch_after=False) else 1
    return run_install_gui()


if __name__ == "__main__":
    sys.exit(main())
