"""RuleProxy 主窗口。"""
from __future__ import annotations

import time
from typing import List

from PyQt6.QtCore import Qt, QTimer
from PyQt6.QtGui import QColor, QIcon, QTextCursor
from PyQt6.QtWidgets import (
    QAbstractItemView,
    QApplication,
    QCheckBox,
    QComboBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMenu,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QSpinBox,
    QSystemTrayIcon,
    QTabWidget,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from ..core.config import APP_NAME, AppConfig, save_config
from ..core.server import ProxyEngine
from ..core import autostart, winproxy
from ..resources import resource_path
from .dialogs import ProxyDialog, RuleDialog
from .styles import LIGHT_QSS

MATCH_LABELS = {
    "process": "应用进程",
    "dest_port": "目标端口",
    "dest_host": "目标主机",
    "src_port": "源端口",
}
ACTION_LABELS = {"direct": "直连", "proxy": "代理", "block": "阻止"}
DEFAULT_ACTION_LABELS = {"direct": "直连（默认）", "proxy": "代理（默认）", "block": "阻止（默认）"}

# 连接表格最多显示的历史条数（限制刷新与内存开销）
MAX_DISPLAY_ROWS = 200


def format_bytes(n: int) -> str:
    n = float(n)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{int(n)} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} TB"


class MainWindow(QMainWindow):
    def __init__(self, cfg: AppConfig):
        super().__init__()
        self.cfg = cfg
        self.engine = ProxyEngine(lambda: self.cfg)
        self.setWindowTitle(f"{APP_NAME} — 分应用 / 分端口代理")
        self.resize(1080, 680)
        self.setStyleSheet(LIGHT_QSS)
        self._really_quit = False
        self._tray: QSystemTrayIcon | None = None
        self._tray_status_act = None
        self._connections_tab: QWidget | None = None
        self._build_ui()
        self._reload_rules()
        self._reload_proxies()
        self._sync_proxy_buttons()
        self._setup_tray()

        self._timer = QTimer(self)
        self._timer.timeout.connect(self._on_tick)
        self._timer.start(1000)
        self.statusBar().showMessage("代理未启动（点左上角“启动代理”）")

    # ---------------------------------------------------------------- UI 构建
    def _build_ui(self) -> None:
        central = QWidget()
        root = QVBoxLayout(central)
        root.setContentsMargins(10, 10, 10, 10)
        root.setSpacing(8)
        root.addLayout(self._build_topbar())
        self.tabs = QTabWidget()
        self.tabs.addTab(self._build_connections_tab(), "连接")
        self.tabs.addTab(self._build_rules_tab(), "规则")
        self.tabs.addTab(self._build_proxies_tab(), "上游代理")
        self.tabs.addTab(self._build_settings_tab(), "设置")
        self.tabs.addTab(self._build_log_tab(), "日志")
        root.addWidget(self.tabs, 1)
        self.setCentralWidget(central)

    def _build_topbar(self) -> QHBoxLayout:
        bar = QHBoxLayout()
        bar.setSpacing(10)

        self.btn_start = QPushButton("启动代理")
        self.btn_start.setObjectName("primary")
        self.btn_start.setFixedWidth(110)
        self.btn_start.clicked.connect(self._toggle_engine)

        self.lbl_listen = QLabel()
        self.lbl_listen.setObjectName("muted")
        self._update_listen_label()

        bar.addWidget(self.btn_start)
        bar.addWidget(self.lbl_listen)
        bar.addStretch(1)

        self.btn_sys_on = QPushButton("设置系统代理")
        self.btn_sys_on.clicked.connect(self._sys_on)
        self.btn_sys_off = QPushButton("取消系统代理")
        self.btn_sys_off.setObjectName("danger")
        self.btn_sys_off.clicked.connect(self._sys_off)

        self.lbl_stats = QLabel("上行 0 B　下行 0 B")
        self.lbl_stats.setObjectName("muted")

        bar.addWidget(self.btn_sys_on)
        bar.addWidget(self.btn_sys_off)
        bar.addWidget(self.lbl_stats)
        return bar

    def _build_connections_tab(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        self.conn_table = QTableWidget(0, 10)
        self.conn_table.setHorizontalHeaderLabels(
            ["时间", "PID", "进程", "目标", "源端口", "命中规则", "动作", "上行", "下行", "状态"]
        )
        self.conn_table.verticalHeader().setVisible(False)
        self.conn_table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.conn_table.setAlternatingRowColors(True)
        self.conn_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        hdr = self.conn_table.horizontalHeader()
        hdr.setSectionResizeMode(QHeaderView.ResizeMode.ResizeToContents)
        hdr.setSectionResizeMode(3, QHeaderView.ResizeMode.Stretch)
        lay.addWidget(self.conn_table)

        tip = QLabel(
            "提示：规则命中即实时生效。应用规则支持进程名 / 单个 exe 文件 / 整个文件夹"
            "（文件夹内所有程序都生效，路径末尾以 \\ 表示文件夹）。"
        )
        tip.setObjectName("muted")
        tip.setWordWrap(True)
        lay.addWidget(tip)
        self._connections_tab = w
        return w

    def _build_rules_tab(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        self.rules_table = QTableWidget(0, 7)
        self.rules_table.setHorizontalHeaderLabels(["启用", "名称", "匹配类型", "匹配值", "动作", "上游代理", "备注"])
        self.rules_table.verticalHeader().setVisible(False)
        self.rules_table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.rules_table.setAlternatingRowColors(True)
        self.rules_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.rules_table.doubleClicked.connect(lambda _: self._edit_rule())
        hdr = self.rules_table.horizontalHeader()
        hdr.setSectionResizeMode(QHeaderView.ResizeMode.ResizeToContents)
        hdr.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        hdr.setSectionResizeMode(6, QHeaderView.ResizeMode.Stretch)
        lay.addWidget(self.rules_table)

        btns = QHBoxLayout()
        for text, slot, obj in (
            ("新增规则", self._add_rule, "primary"),
            ("编辑", self._edit_rule, ""),
            ("删除", self._delete_rule, "danger"),
            ("上移", self._move_rule_up, ""),
            ("下移", self._move_rule_down, ""),
            ("启用/停用", self._toggle_rule, ""),
        ):
            b = QPushButton(text)
            if obj:
                b.setObjectName(obj)
            b.clicked.connect(slot)
            btns.addWidget(b)
        btns.addStretch(1)
        lay.addLayout(btns)
        return w

    def _build_proxies_tab(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        self.proxies_table = QTableWidget(0, 6)
        self.proxies_table.setHorizontalHeaderLabels(["启用", "名称", "协议", "主机", "端口", "用户名"])
        self.proxies_table.verticalHeader().setVisible(False)
        self.proxies_table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.proxies_table.setAlternatingRowColors(True)
        self.proxies_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.proxies_table.doubleClicked.connect(lambda _: self._edit_proxy())
        hdr = self.proxies_table.horizontalHeader()
        hdr.setSectionResizeMode(QHeaderView.ResizeMode.ResizeToContents)
        hdr.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        lay.addWidget(self.proxies_table)

        btns = QHBoxLayout()
        for text, slot, obj in (
            ("新增代理", self._add_proxy, "primary"),
            ("编辑", self._edit_proxy, ""),
            ("删除", self._delete_proxy, "danger"),
        ):
            b = QPushButton(text)
            if obj:
                b.setObjectName(obj)
            b.clicked.connect(slot)
            btns.addWidget(b)
        btns.addStretch(1)
        lay.addLayout(btns)
        return w

    def _build_settings_tab(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)

        grp = QGroupBox("监听设置（修改后需重启代理生效）")
        form = QFormLayout(grp)
        self.ed_host = QLineEdit(self.cfg.listen_host)
        self.sp_http = QSpinBox()
        self.sp_http.setRange(1, 65535)
        self.sp_http.setValue(self.cfg.http_port)
        self.sp_socks = QSpinBox()
        self.sp_socks.setRange(1, 65535)
        self.sp_socks.setValue(self.cfg.socks5_port)
        form.addRow("监听地址", self.ed_host)
        form.addRow("HTTP 代理端口", self.sp_http)
        form.addRow("SOCKS5 代理端口", self.sp_socks)
        lay.addWidget(grp)

        grp2 = QGroupBox("默认行为（未命中任何规则时的处理）")
        form2 = QFormLayout(grp2)
        self.cb_default_action = QComboBox()
        for value, label in DEFAULT_ACTION_LABELS.items():
            self.cb_default_action.addItem(label, value)
        idx = self.cb_default_action.findData(self.cfg.default_action)
        self.cb_default_action.setCurrentIndex(max(idx, 0))
        self.cb_default_proxy = QComboBox()
        form2.addRow("默认动作", self.cb_default_action)
        form2.addRow("默认代理（动作=代理时）", self.cb_default_proxy)
        lay.addWidget(grp2)

        grp_launch = QGroupBox("启动选项")
        lay_launch = QVBoxLayout(grp_launch)
        self.cb_autostart = QCheckBox("开机自启动（Windows 登录后自动运行，最小化到托盘）")
        self.cb_autostart.setChecked(autostart.is_enabled())
        self.cb_start_minimized = QCheckBox("启动时最小化到托盘（不显示主窗口）")
        self.cb_start_minimized.setChecked(self.cfg.start_minimized)
        lay_launch.addWidget(self.cb_autostart)
        lay_launch.addWidget(self.cb_start_minimized)
        lay.addWidget(grp_launch)

        btn_save = QPushButton("保存设置")
        btn_save.setObjectName("primary")
        btn_save.clicked.connect(self._save_settings)
        lay.addWidget(btn_save)

        grp3 = QGroupBox("使用说明")
        note = QLabel(
            "1. 点“启动代理”后，本工具在本地监听 HTTP / SOCKS5 代理端口。\n"
            "2. 点“设置系统代理”，让系统应用（浏览器等）的流量先进入本工具。\n"
            "3. 在“规则”里配置：某进程 / 某端口 / 某域名 → 直连 或 代理。\n"
            "4. 例：目标端口 8080 → 代理；目标端口 80、443 → 直连。\n\n"
            "注意：Windows 系统代理只对遵循系统代理的应用生效；"
            "不遵循系统代理的应用（部分游戏/原生程序），请在其设置里手动填写代理地址。\n"
            "规则修改即时生效，无需重启代理。"
        )
        note.setObjectName("muted")
        note.setWordWrap(True)
        grp3.setLayout(QVBoxLayout())
        grp3.layout().addWidget(note)
        lay.addWidget(grp3)
        lay.addStretch(1)
        return w

    def _build_log_tab(self) -> QWidget:
        w = QWidget()
        lay = QVBoxLayout(w)
        self.log_view = QPlainTextEdit()
        self.log_view.setReadOnly(True)
        self.log_view.setMaximumBlockCount(2000)
        lay.addWidget(self.log_view)
        return w

    # ---------------------------------------------------------------- 引擎控制
    def _toggle_engine(self) -> None:
        if self.engine.running:
            self.engine.stop()
            self.btn_start.setText("启动代理")
            self.statusBar().showMessage("代理已停止")
        else:
            self.engine.start()
            if self.engine.running:
                self.btn_start.setText("停止代理")
                self.statusBar().showMessage("代理运行中")
            else:
                self.btn_start.setText("启动代理")
                self.statusBar().showMessage("代理启动失败，请检查端口是否被占用")
        self._update_listen_label()
        self._update_tray_status()

    def _update_listen_label(self) -> None:
        self.lbl_listen.setText(
            f"监听 {self.cfg.listen_host}:{self.cfg.http_port} (HTTP) / "
            f"{self.cfg.socks5_port} (SOCKS5)"
        )

    # ---------------------------------------------------------------- 系统代理
    def _sys_on(self) -> None:
        if not self.engine.running:
            QMessageBox.warning(self, "提示", "请先启动代理，再设置系统代理。")
            return
        if winproxy.set_system_proxy(self.cfg.listen_host, self.cfg.http_port):
            self.statusBar().showMessage(
                f"已设置系统代理为 {self.cfg.listen_host}:{self.cfg.http_port}"
            )
            self.log_line("已设置系统代理")
        else:
            QMessageBox.warning(self, "提示", "设置系统代理失败（当前仅支持 Windows）。")
        self._sync_proxy_buttons()

    def _sys_off(self) -> None:
        if winproxy.clear_system_proxy():
            self.statusBar().showMessage("已取消系统代理")
            self.log_line("已取消系统代理")
        else:
            QMessageBox.warning(self, "提示", "取消系统代理失败（当前仅支持 Windows）。")
        self._sync_proxy_buttons()

    def _sync_proxy_buttons(self) -> None:
        on = winproxy.is_system_proxy_on()
        self.btn_sys_on.setEnabled(not on)
        self.btn_sys_off.setEnabled(on)

    # ---------------------------------------------------------------- 规则管理
    def _proxy_names(self) -> List[str]:
        return [p.name for p in self.cfg.proxies]

    def _reload_rules(self) -> None:
        self.rules_table.setRowCount(len(self.cfg.rules))
        for r, rule in enumerate(self.cfg.rules):
            values = [
                "✓" if rule.enabled else "✗",
                rule.name,
                MATCH_LABELS.get(rule.match_type, rule.match_type),
                rule.match_value,
                ACTION_LABELS.get(rule.action, rule.action),
                rule.proxy or "自动",
                rule.note,
            ]
            for c, text in enumerate(values):
                self.rules_table.setItem(r, c, QTableWidgetItem(text))

    def _selected_rule_index(self) -> int:
        row = self.rules_table.currentRow()
        if row < 0 or row >= len(self.cfg.rules):
            return -1
        return row

    def _add_rule(self) -> None:
        dlg = RuleDialog(self, proxy_names=self._proxy_names())
        if dlg.exec():
            self.cfg.rules.append(dlg.result_rule())
            save_config(self.cfg)
            self._reload_rules()
            self.log_line(f"已新增规则：{self.cfg.rules[-1].name}")

    def _edit_rule(self) -> None:
        idx = self._selected_rule_index()
        if idx < 0:
            return
        dlg = RuleDialog(self, rule=self.cfg.rules[idx], proxy_names=self._proxy_names())
        if dlg.exec():
            self.cfg.rules[idx] = dlg.result_rule()
            save_config(self.cfg)
            self._reload_rules()

    def _delete_rule(self) -> None:
        idx = self._selected_rule_index()
        if idx < 0:
            return
        name = self.cfg.rules[idx].name
        if QMessageBox.question(self, "确认", f"删除规则「{name}」？") == QMessageBox.StandardButton.Yes:
            del self.cfg.rules[idx]
            save_config(self.cfg)
            self._reload_rules()
            self.log_line(f"已删除规则：{name}")

    def _move_rule(self, delta: int) -> None:
        idx = self._selected_rule_index()
        if idx < 0:
            return
        new = idx + delta
        if new < 0 or new >= len(self.cfg.rules):
            return
        self.cfg.rules[idx], self.cfg.rules[new] = self.cfg.rules[new], self.cfg.rules[idx]
        save_config(self.cfg)
        self._reload_rules()
        self.rules_table.setCurrentCell(new, 0)

    def _move_rule_up(self) -> None:
        self._move_rule(-1)

    def _move_rule_down(self) -> None:
        self._move_rule(1)

    def _toggle_rule(self) -> None:
        idx = self._selected_rule_index()
        if idx < 0:
            return
        rule = self.cfg.rules[idx]
        rule.enabled = not rule.enabled
        save_config(self.cfg)
        self._reload_rules()
        self.rules_table.setCurrentCell(idx, 0)

    # ---------------------------------------------------------------- 代理管理
    def _reload_proxies(self) -> None:
        self.proxies_table.setRowCount(len(self.cfg.proxies))
        for r, p in enumerate(self.cfg.proxies):
            values = ["✓" if p.enabled else "✗", p.name, p.type.upper(), p.host, str(p.port), p.username]
            for c, text in enumerate(values):
                self.proxies_table.setItem(r, c, QTableWidgetItem(text))
        self._reload_proxy_combo()

    def _reload_proxy_combo(self) -> None:
        cur = self.cb_default_proxy.currentData()
        self.cb_default_proxy.clear()
        self.cb_default_proxy.addItem("（自动选择第一个可用）", "")
        for p in self.cfg.proxies:
            self.cb_default_proxy.addItem(p.name, p.name)
        if cur:
            idx = self.cb_default_proxy.findData(cur)
            if idx >= 0:
                self.cb_default_proxy.setCurrentIndex(idx)

    def _selected_proxy_index(self) -> int:
        row = self.proxies_table.currentRow()
        if row < 0 or row >= len(self.cfg.proxies):
            return -1
        return row

    def _add_proxy(self) -> None:
        dlg = ProxyDialog(self)
        if dlg.exec():
            self.cfg.proxies.append(dlg.result_proxy())
            save_config(self.cfg)
            self._reload_proxies()
            self.log_line(f"已新增上游代理：{self.cfg.proxies[-1].name}")

    def _edit_proxy(self) -> None:
        idx = self._selected_proxy_index()
        if idx < 0:
            return
        dlg = ProxyDialog(self, proxy=self.cfg.proxies[idx])
        if dlg.exec():
            self.cfg.proxies[idx] = dlg.result_proxy()
            save_config(self.cfg)
            self._reload_proxies()

    def _delete_proxy(self) -> None:
        idx = self._selected_proxy_index()
        if idx < 0:
            return
        name = self.cfg.proxies[idx].name
        if QMessageBox.question(self, "确认", f"删除上游代理「{name}」？") == QMessageBox.StandardButton.Yes:
            del self.cfg.proxies[idx]
            save_config(self.cfg)
            self._reload_proxies()
            self.log_line(f"已删除上游代理：{name}")

    # ---------------------------------------------------------------- 设置保存
    def _save_settings(self) -> None:
        self.cfg.listen_host = self.ed_host.text().strip() or "127.0.0.1"
        self.cfg.http_port = self.sp_http.value()
        self.cfg.socks5_port = self.sp_socks.value()
        self.cfg.default_action = self.cb_default_action.currentData()
        self.cfg.default_proxy = self.cb_default_proxy.currentData() or ""
        # 启动选项
        if self.cb_autostart.isChecked():
            if not autostart.enable():
                QMessageBox.warning(self, "提示", "设置开机自启动失败（当前仅支持 Windows）。")
        else:
            autostart.disable()
        self.cfg.start_minimized = self.cb_start_minimized.isChecked()
        save_config(self.cfg)
        self._update_listen_label()
        if self.engine.running:
            QMessageBox.information(
                self, "提示",
                "监听设置已保存。端口/地址修改需“停止代理”后重新“启动代理”生效。",
            )
        else:
            self.statusBar().showMessage("设置已保存")

    # ---------------------------------------------------------------- 定时刷新
    def _on_tick(self) -> None:
        # 日志始终同步（避免重开窗口后日志缺失）
        self._drain_logs()
        # 界面仅在窗口可见时刷新：最小化到托盘时暂停，显著降低资源占用
        if self.isVisible():
            self._refresh_connections()

    def _drain_logs(self) -> None:
        items = self.engine.drain_logs()
        if not items:
            return
        # 一次性批量插入，避免逐行触发文档重排
        lines = "".join(
            f"[{time.strftime('%H:%M:%S', time.localtime(ts))}] {msg}\n"
            for ts, msg in items
        )
        cur = self.log_view.textCursor()
        cur.movePosition(QTextCursor.MoveOperation.End)
        self.log_view.setTextCursor(cur)
        self.log_view.insertPlainText(lines)

    def log_line(self, msg: str) -> None:
        self.log_view.appendPlainText(f"[{time.strftime('%H:%M:%S')}] {msg}")

    STATUS_COLORS = {"已连接": "#2ecc71", "已断开": "#7f8c8d", "已停止": "#7f8c8d"}

    def _refresh_connections(self) -> None:
        table = self.conn_table
        if not self.engine.running:
            if table.rowCount() != 0:
                table.setUpdatesEnabled(False)
                table.setRowCount(0)
                table.setUpdatesEnabled(True)
                table.viewport().update()
            self.lbl_stats.setText("上行 0 B　下行 0 B")
            return

        snap = self.engine.snapshot()
        # 顶栏统计始终更新
        self.lbl_stats.setText(
            f"上行 {format_bytes(snap['total_up'])}　下行 {format_bytes(snap['total_down'])}"
        )
        # 表格不可见（正停留在其他标签页）时不重建，进一步减负
        if self.tabs.currentWidget() is not self._connections_tab:
            return

        # 只显示最近 MAX_DISPLAY_ROWS 条历史，控制表格规模
        rows = snap["active"] + snap["history"][:MAX_DISPLAY_ROWS]
        n = len(rows)
        table.setUpdatesEnabled(False)
        try:
            if table.rowCount() != n:
                table.setRowCount(n)
            for r, rec in enumerate(rows):
                dst = f"{rec['dst_host']}:{rec['dst_port']}" if rec["dst_port"] else "-"
                values = [
                    rec["ts"],
                    str(rec["pid"] or "-"),
                    rec["process"] or "-",
                    dst,
                    str(rec["src_port"]),
                    rec["rule"] or "-",
                    ACTION_LABELS.get(rec["action"], rec["action"] or "-"),
                    format_bytes(rec["up"]),
                    format_bytes(rec["down"]),
                    rec["status"],
                ]
                for c, text in enumerate(values):
                    it = table.item(r, c)
                    if it is None:
                        it = QTableWidgetItem()
                        table.setItem(r, c, it)
                    # 仅在内容变化时更新，避免反复重建与重绘
                    if it.text() != text:
                        it.setText(text)
                    if c == 9:
                        color = self.STATUS_COLORS.get(rec["status"], "#e67e22")
                        if it.foreground().color().name() != color:
                            it.setForeground(QColor(color))
        finally:
            table.setUpdatesEnabled(True)
        table.viewport().update()

    # ---------------------------------------------------------------- 系统托盘
    def _setup_tray(self) -> None:
        """在系统托盘创建图标（运行时生效），支持最小化到托盘与完全退出。"""
        if not QSystemTrayIcon.isSystemTrayAvailable():
            return
        icon = QIcon(resource_path("icon.ico"))
        tray = QSystemTrayIcon(icon, self)

        menu = QMenu()
        # 状态项：直观显示代理是否在后台运行（托盘化后依然生效）
        self._tray_status_act = menu.addAction("代理未启动")
        self._tray_status_act.setCheckable(True)
        self._tray_status_act.setEnabled(False)  # 仅作状态显示
        menu.addSeparator()
        act_show = menu.addAction("显示主界面")
        act_show.triggered.connect(self._show_main)
        act_toggle = menu.addAction("启动 / 停止代理")
        act_toggle.triggered.connect(self._toggle_engine)
        menu.addSeparator()
        act_quit = menu.addAction("完全退出")
        act_quit.triggered.connect(self._quit_app)

        tray.setContextMenu(menu)
        tray.activated.connect(self._on_tray_activated)
        self._tray = tray
        self._update_tray_status()
        tray.show()

    def _update_tray_status(self) -> None:
        """更新托盘状态：代理是否在后台运行（托盘化后依然生效）。"""
        if not self._tray:
            return
        running = self.engine.running
        tip = f"{APP_NAME} — {'代理运行中（托盘常驻）' if running else '代理未启动'}"
        self._tray.setToolTip(tip)
        if self._tray_status_act:
            self._tray_status_act.setText("代理运行中（托盘常驻）" if running else "代理未启动")
            self._tray_status_act.setChecked(running)

    def _show_main(self) -> None:
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def start_in_tray(self) -> None:
        """启动即最小化到托盘：不显示主窗口，仅托盘运行。"""
        if self._tray:
            self._tray.showMessage(
                APP_NAME,
                "已最小化到托盘运行，右键托盘图标可打开主界面 / 完全退出。",
                QSystemTrayIcon.MessageIcon.Information,
                3000,
            )

    def _on_tray_activated(self, reason) -> None:  # noqa: N802
        # 单击 / 双击托盘图标恢复主窗口
        if reason in (
            QSystemTrayIcon.ActivationReason.Trigger,
            QSystemTrayIcon.ActivationReason.DoubleClick,
        ):
            self._show_main()

    def _quit_app(self) -> None:
        """从托盘菜单完全退出。"""
        self._really_quit = True
        if self._tray:
            self._tray.hide()
        self.engine.stop()
        QApplication.quit()

    def closeEvent(self, event) -> None:  # noqa: N802
        # 有托盘时，点 X 只最小化到托盘；从托盘“完全退出”时才真正关闭
        if self._tray and self._tray.isVisible() and not self._really_quit:
            event.ignore()
            self.hide()
            self._tray.showMessage(
                APP_NAME,
                "已最小化到托盘，程序仍在后台运行；右键托盘图标可“完全退出”。",
                QSystemTrayIcon.MessageIcon.Information,
                3000,
            )
        else:
            self.engine.stop()
            super().closeEvent(event)
