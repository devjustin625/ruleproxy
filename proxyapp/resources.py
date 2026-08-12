"""资源路径解析：兼容源码运行与 PyInstaller 打包后的 exe。

- 源码运行：资源在项目根目录（proxyapp 的上一级）
- 打包后：资源被 --add-data 解压到 sys._MEIPASS 临时目录
"""
from __future__ import annotations

import os
import sys


def resource_path(rel: str) -> str:
    """返回相对资源（如 icon.ico）在源码 / 打包环境中的绝对路径。"""
    if getattr(sys, "frozen", False):
        base = getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
    else:
        base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, rel)
