"""双向数据转发（单线程多路复用，显著降低线程开销）。"""
from __future__ import annotations

import selectors
import socket
import time
from typing import Callable, List, Optional

# 空闲连接保留时长（秒）：超过则断开以释放线程
IDLE_TIMEOUT = 300.0


def tunnel(
    a: socket.socket,
    b: socket.socket,
    up_counter: List[int],
    down_counter: List[int],
    on_close: Optional[Callable[[], None]] = None,
) -> None:
    """a<->b 全双工转发（单线程 selectors 多路复用）。

    up_counter 统计 a→b 的字节数，down_counter 统计 b→a 的字节数。
    支持半关闭（一端 EOF 后仍继续转发另一方向），任一端断开 / 空闲超时后
    关闭两端并调用 on_close。相比每个连接 2 个转发线程，改为 1 个线程内
    完成双向转发，高并发时显著减少线程创建与切换开销。
    """
    # 加大收发缓冲、单次读取量，并关闭 Nagle 降低小包延迟（尽量不拖累网页打开）
    for s in (a, b):
        try:
            s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        except OSError:
            pass
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 256 * 1024)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 256 * 1024)
        except OSError:
            pass
    sel = selectors.DefaultSelector()
    sel.register(a, selectors.EVENT_READ, (a, b, up_counter))
    sel.register(b, selectors.EVENT_READ, (b, a, down_counter))
    last_active = time.monotonic()
    try:
        while True:
            events = sel.select(timeout=1.0)
            now = time.monotonic()
            if not events:
                if now - last_active > IDLE_TIMEOUT:
                    return  # 空闲超时
                continue
            last_active = now
            for key, _ in events:
                src, dst, counter = key.data
                try:
                    data = src.recv(131072)
                except (ConnectionError, OSError, ValueError):
                    data = b""
                if data:
                    dst.sendall(data)
                    counter[0] += len(data)
                else:
                    # 该方向 EOF：停止读取，向对端半关闭；另一方向继续
                    try:
                        sel.unregister(src)
                    except Exception:
                        pass
                    try:
                        dst.shutdown(socket.SHUT_WR)
                    except OSError:
                        pass
            if not sel.get_map():
                return
    except (ConnectionError, OSError, ValueError):
        pass
    finally:
        sel.close()
        for s in (a, b):
            try:
                s.close()
            except OSError:
                pass
        if on_close:
            try:
                on_close()
            except Exception:
                pass
