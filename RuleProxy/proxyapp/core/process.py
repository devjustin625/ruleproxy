"""进程检测：根据客户端源端口找到所属应用进程。"""
from __future__ import annotations

import time
from typing import Optional, Tuple

try:
    import psutil
except Exception:  # pragma: no cover
    psutil = None


class ProcessDetector:
    """维护 (源端口 -> (pid, 进程名)) 映射，定时刷新。

    当客户端（应用）连接到本代理时，其在本地有一个临时源端口；
    通过 psutil.net_connections 可查到该源端口属于哪个进程，
    从而实现“分应用”规则。
    """

    REFRESH_INTERVAL = 1.0          # 后台全量刷新的最短间隔
    MIN_SCAN_INTERVAL = 0.5         # 热路径补扫的最短间隔（限流，防抖）

    def __init__(self) -> None:
        self._map: dict[int, Tuple[int, str]] = {}
        self._last_refresh = 0.0
        self._name_cache: dict[int, str] = {}
        self._exe_cache: dict[int, str] = {}

    @property
    def available(self) -> bool:
        return psutil is not None

    def refresh(self, force: bool = False) -> None:
        if psutil is None:
            return
        now = time.time()
        if not force and now - self._last_refresh < self.REFRESH_INTERVAL:
            return
        self._last_refresh = now
        try:
            conns = psutil.net_connections(kind="tcp")
        except Exception:
            return
        m: dict[int, Tuple[int, str]] = {}
        for c in conns:
            if c.laddr and c.pid is not None:
                # 客户端（非监听）连接：laddr 端口即源端口
                m[c.laddr.port] = (c.pid, self._name(c.pid))
        if m:
            self._map = m

    def _name(self, pid: int) -> str:
        if pid in self._name_cache:
            return self._name_cache[pid]
        name = str(pid)
        if psutil is not None:
            try:
                name = psutil.Process(pid).name()
            except Exception:
                name = str(pid)
        self._name_cache[pid] = name
        if len(self._name_cache) > 2000:
            self._name_cache.clear()
        return name

    def _exe(self, pid: int) -> str:
        """返回进程可执行文件完整路径（带缓存），解析失败时返回空字符串。"""
        if pid in self._exe_cache:
            return self._exe_cache[pid]
        path = ""
        if psutil is not None:
            try:
                path = psutil.Process(pid).exe()
            except Exception:
                path = ""
        self._exe_cache[pid] = path
        if len(self._exe_cache) > 2000:
            self._exe_cache.clear()
        return path

    def process_for_port(self, port: int, need_exe: bool = False, allow_scan: bool = False) -> Tuple[Optional[int], str, str]:
        """返回 (pid, 进程名, exe 路径)；找不到时返回 (None, '', '')。

        默认只查后台线程（每 1s）维护的映射，热路径零 psutil 开销。
        存在“应用进程”规则时（allow_scan=True），映射未命中才限流补扫，
        保证按进程分流可靠。need_exe=True 时按需解析 exe 路径（有缓存）。
        """
        if psutil is None:
            return None, "", ""
        info = self._map.get(port)
        if info is None and allow_scan:
            if time.time() - self._last_refresh >= self.MIN_SCAN_INTERVAL:
                self.refresh(force=True)
            info = self._map.get(port)
            if info is None:
                time.sleep(0.05)  # 极短等待后重试一次，捕获刚建立的连接
                self.refresh(force=True)
                info = self._map.get(port)
        if info is None:
            return None, "", ""
        pid, name = info
        exe = self._exe(pid) if need_exe else ""
        return pid, name, exe
