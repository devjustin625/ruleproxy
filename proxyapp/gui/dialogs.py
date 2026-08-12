"""规则与代理配置对话框。"""
from __future__ import annotations

import os
from typing import List, Optional

from PyQt6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFormLayout,
    QHBoxLayout,
    QLineEdit,
    QPushButton,
    QSpinBox,
    QVBoxLayout,
    QWidget,
)

from ..core.config import Rule, UpstreamConfig

MATCH_TYPES = [
    ("process", "应用进程（名 / exe 文件 / 文件夹）"),
    ("dest_port", "目标端口"),
    ("dest_host", "目标主机 / 域名"),
    ("src_port", "客户端源端口"),
]
ACTIONS = [("direct", "直连"), ("proxy", "代理"), ("block", "阻止")]
PROXY_TYPES = [("http", "HTTP 代理"), ("socks5", "SOCKS5 代理")]


def _make_buttons(parent: QDialog) -> QDialogButtonBox:
    box = QDialogButtonBox(
        QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
    )
    box.accepted.connect(parent.accept)
    box.rejected.connect(parent.reject)
    return box


class RuleDialog(QDialog):
    def __init__(self, parent=None, rule: Optional[Rule] = None, proxy_names: List[str] = ()):
        super().__init__(parent)
        self.setWindowTitle("编辑规则")
        self.setMinimumWidth(460)

        root = QVBoxLayout(self)
        form = QFormLayout()
        form.setSpacing(10)

        self.name_edit = QLineEdit()
        self.enable_check = QCheckBox("启用该规则")
        self.type_combo = QComboBox()
        for value, label in MATCH_TYPES:
            self.type_combo.addItem(label, value)
        self.value_edit = QLineEdit()
        self.value_edit.setPlaceholderText(
            "端口：8080 或 80,443 或 8000-9000　进程：chrome 或 exe/文件夹路径　域名：*.example.com"
        )
        self.btn_file = QPushButton("选择文件…")
        self.btn_file.clicked.connect(self._pick_file)
        self.btn_folder = QPushButton("选择文件夹…")
        self.btn_folder.clicked.connect(self._pick_folder)
        value_row = QHBoxLayout()
        value_row.setSpacing(6)
        value_row.addWidget(self.value_edit, 1)
        value_row.addWidget(self.btn_file)
        value_row.addWidget(self.btn_folder)
        value_box = QWidget()
        value_box.setLayout(value_row)
        self.action_combo = QComboBox()
        for value, label in ACTIONS:
            self.action_combo.addItem(label, value)
        self.proxy_combo = QComboBox()
        self.proxy_combo.addItem("（自动选择第一个可用）", "")
        for n in proxy_names:
            self.proxy_combo.addItem(n, n)
        self.note_edit = QLineEdit()

        form.addRow("规则名称", self.name_edit)
        form.addRow("启用", self.enable_check)
        form.addRow("匹配类型", self.type_combo)
        form.addRow("匹配值", value_box)
        form.addRow("动作", self.action_combo)
        form.addRow("上游代理", self.proxy_combo)
        form.addRow("备注", self.note_edit)
        root.addLayout(form)

        self.type_combo.currentIndexChanged.connect(self._sync_controls)
        self.action_combo.currentIndexChanged.connect(self._sync_controls)
        root.addWidget(_make_buttons(self))

        if rule:
            self.name_edit.setText(rule.name)
            self.enable_check.setChecked(rule.enabled)
            idx = self.type_combo.findData(rule.match_type)
            self.type_combo.setCurrentIndex(max(idx, 0))
            self.value_edit.setText(rule.match_value)
            aidx = self.action_combo.findData(rule.action)
            self.action_combo.setCurrentIndex(max(aidx, 0))
            if rule.proxy:
                pidx = self.proxy_combo.findData(rule.proxy)
                if pidx >= 0:
                    self.proxy_combo.setCurrentIndex(pidx)
                else:
                    self.proxy_combo.addItem(rule.proxy, rule.proxy)
                    self.proxy_combo.setCurrentIndex(self.proxy_combo.count() - 1)
            self.note_edit.setText(rule.note)
        else:
            self.name_edit.setText("新规则")
            self.enable_check.setChecked(True)
        self._sync_controls()

    def _sync_controls(self) -> None:
        self.proxy_combo.setEnabled(self.action_combo.currentData() == "proxy")
        is_process = self.type_combo.currentData() == "process"
        self.btn_file.setVisible(is_process)
        self.btn_folder.setVisible(is_process)

    def _pick_file(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self, "选择应用文件", "", "应用程序 (*.exe);;所有文件 (*.*)"
        )
        if path:
            self.value_edit.setText(path)

    def _pick_folder(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "选择文件夹")
        if path:
            # 以分隔符结尾，明确标识为“文件夹”（其内所有程序适用）
            if not path.endswith(os.sep):
                path += os.sep
            self.value_edit.setText(path)

    def result_rule(self) -> Rule:
        return Rule(
            name=self.name_edit.text().strip() or "未命名规则",
            enabled=self.enable_check.isChecked(),
            match_type=self.type_combo.currentData(),
            match_value=self.value_edit.text().strip(),
            action=self.action_combo.currentData(),
            proxy=self.proxy_combo.currentData() or "",
            note=self.note_edit.text().strip(),
        )


class ProxyDialog(QDialog):
    def __init__(self, parent=None, proxy: Optional[UpstreamConfig] = None):
        super().__init__(parent)
        self.setWindowTitle("编辑上游代理")
        self.setMinimumWidth(420)

        root = QVBoxLayout(self)
        form = QFormLayout()
        form.setSpacing(10)

        self.name_edit = QLineEdit()
        self.enable_check = QCheckBox("启用该代理")
        self.type_combo = QComboBox()
        for value, label in PROXY_TYPES:
            self.type_combo.addItem(label, value)
        self.host_edit = QLineEdit()
        self.port_spin = QSpinBox()
        self.port_spin.setRange(1, 65535)
        self.user_edit = QLineEdit()
        self.pass_edit = QLineEdit()
        self.pass_edit.setEchoMode(QLineEdit.EchoMode.Password)

        form.addRow("代理名称", self.name_edit)
        form.addRow("启用", self.enable_check)
        form.addRow("协议类型", self.type_combo)
        form.addRow("主机地址", self.host_edit)
        form.addRow("端口", self.port_spin)
        form.addRow("用户名（可选）", self.user_edit)
        form.addRow("密码（可选）", self.pass_edit)
        root.addLayout(form)
        root.addWidget(_make_buttons(self))

        if proxy:
            self.name_edit.setText(proxy.name)
            self.enable_check.setChecked(proxy.enabled)
            idx = self.type_combo.findData(proxy.type)
            self.type_combo.setCurrentIndex(max(idx, 0))
            self.host_edit.setText(proxy.host)
            self.port_spin.setValue(proxy.port)
            self.user_edit.setText(proxy.username)
            self.pass_edit.setText(proxy.password)
        else:
            self.name_edit.setText("新代理")
            self.enable_check.setChecked(True)
            self.port_spin.setValue(7890)

    def result_proxy(self) -> UpstreamConfig:
        return UpstreamConfig(
            name=self.name_edit.text().strip() or "未命名代理",
            enabled=self.enable_check.isChecked(),
            type=self.type_combo.currentData(),
            host=self.host_edit.text().strip() or "127.0.0.1",
            port=self.port_spin.value(),
            username=self.user_edit.text().strip(),
            password=self.pass_edit.text(),
        )
