"""白色现代主题样式。"""

LIGHT_QSS = """
QMainWindow, QDialog { background: #f5f6f8; }
QWidget { color: #1f2329; font-size: 13px; }
QTabWidget::pane { border: 1px solid #d9dde3; background: #ffffff; top: -1px; }
QTabBar::tab { background: #eceff3; padding: 8px 18px; margin-right: 2px;
               border-top-left-radius: 6px; border-top-right-radius: 6px;
               color: #4a4f57; }
QTabBar::tab:selected { background: #ffffff; color: #1a56db; font-weight: 600; }
QTableWidget { background: #ffffff; gridline-color: #e3e6ea;
               alternate-background-color: #f7f8fa; selection-background-color: #d6e4ff;
               selection-color: #1f2329; }
QHeaderView::section { background: #f0f2f5; color: #4a4f57; padding: 6px;
                       border: none; border-bottom: 1px solid #d9dde3; font-weight: 600; }
QPushButton { background: #ffffff; border: 1px solid #c9ced6; border-radius: 6px;
              padding: 6px 14px; color: #1f2329; }
QPushButton:hover { background: #eef1f5; }
QPushButton:pressed { background: #e2e6ec; }
QPushButton:disabled { color: #a0a6ae; background: #f0f2f5; border-color: #e3e6ea; }
QPushButton#primary { background: #2f6feb; color: #ffffff; border: none; }
QPushButton#primary:hover { background: #4c82f0; }
QPushButton#danger { background: #d93025; color: #ffffff; border: none; }
QPushButton#danger:hover { background: #e1544a; }
QPushButton#success { background: #1e9e57; color: #ffffff; border: none; }
QPushButton#success:hover { background: #2bb667; }
QLineEdit, QSpinBox, QComboBox { background: #ffffff; border: 1px solid #c9ced6;
                                 border-radius: 6px; padding: 5px 8px;
                                 selection-background-color: #b8d0ff;
                                 selection-color: #1f2329; }
QLineEdit:focus, QSpinBox:focus, QComboBox:focus { border-color: #2f6feb; }
QComboBox QAbstractItemView { background: #ffffff; selection-background-color: #d6e4ff;
                              selection-color: #1f2329; border: 1px solid #c9ced6; }
QPlainTextEdit { background: #fafbfc; border: 1px solid #d9dde3; border-radius: 6px;
                 font-family: Consolas, Consolas, monospace; font-size: 12px;
                 color: #1f2329; }
QGroupBox { border: 1px solid #d9dde3; border-radius: 8px; margin-top: 12px;
            padding-top: 8px; background: #ffffff; }
QGroupBox::title { subcontrol-origin: margin; left: 10px; padding: 0 4px; color: #4a4f57; }
QStatusBar { background: #ffffff; color: #6b7280; }
QStatusBar::item { border: none; }
QCheckBox::indicator { width: 16px; height: 16px; }
QCheckBox::indicator:unchecked { border: 1px solid #c9ced6; background: #ffffff; border-radius: 4px; }
QCheckBox::indicator:checked { background: #2f6feb; border: 1px solid #2f6feb; border-radius: 4px; }
QScrollBar:vertical { background: #f0f2f5; width: 12px; }
QScrollBar::handle:vertical { background: #c4cad1; border-radius: 6px; min-height: 24px; }
QScrollBar::handle:vertical:hover { background: #a9b0ba; }
QScrollBar:horizontal { background: #f0f2f5; height: 12px; }
QScrollBar::handle:horizontal { background: #c4cad1; border-radius: 6px; min-width: 24px; }
QLabel#muted { color: #6b7280; }
QLabel#title { font-size: 15px; font-weight: bold; color: #1f2329; }
"""
