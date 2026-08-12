"""代理引擎：HTTP 代理 + SOCKS5 代理服务器。

每个连接进来后：
  1. 通过客户端源端口反查所属进程（分应用）；
  2. 提取目标主机/端口；
  3. 按规则表决定路由（直连 / 走代理 / 阻止）；
  4. 建立隧道并统计流量。
"""
from __future__ import annotations

import queue
import socket
import socketserver
import threading
import time
import urllib.parse
from collections import deque
from typing import Callable, List, Optional

from .config import AppConfig
from .process import ProcessDetector
from .relay import tunnel
from .routing import needs_process, pick_route, uses_path_rules
from .upstream import connect_via

# 历史连接记录上限（配合 GUI 的 MAX_DISPLAY_ROWS，控制内存与刷新开销）
MAX_HISTORY = 200


def _set_nodelay(sock) -> None:
    """关闭 Nagle 算法，降低小包（HTTP 请求/响应）的传输延迟。"""
    try:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    except OSError:
        pass


class _TCPServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, addr, handler, engine):
        self.engine = engine
        super().__init__(addr, handler)


class _HTTPHandler(socketserver.BaseRequestHandler):
    def handle(self):
        self.server.engine.handle_http(self.request, self.client_address)


class _SocksHandler(socketserver.BaseRequestHandler):
    def handle(self):
        self.server.engine.handle_socks(self.request, self.client_address)


class ProxyEngine:
    def __init__(self, config_getter: Callable[[], AppConfig], on_log: Optional[Callable[[str], None]] = None):
        self._config_getter = config_getter
        self._log_cb = on_log
        self._detector = ProcessDetector()
        self._lock = threading.Lock()
        self._active: dict[int, dict] = {}
        self._history: deque = deque(maxlen=MAX_HISTORY)
        self._log_q: "queue.Queue" = queue.Queue()
        self._next_id = 0
        self._total_up = 0
        self._total_down = 0
        self._http: Optional[_TCPServer] = None
        self._socks: Optional[_TCPServer] = None
        self._refresh_t: Optional[threading.Thread] = None
        self._stopping = False

    # ------------------------------------------------------------------ 生命周期
    @property
    def running(self) -> bool:
        return (self._http is not None or self._socks is not None) and not self._stopping

    def start(self) -> None:
        if self.running:
            return
        cfg = self._config_getter()
        self._stopping = False
        self.log(f"启动代理引擎：HTTP={cfg.listen_host}:{cfg.http_port}  SOCKS5={cfg.listen_host}:{cfg.socks5_port}")
        try:
            self._http = _TCPServer((cfg.listen_host, cfg.http_port), _HTTPHandler, self)
        except OSError as e:
            self.log(f"HTTP 代理启动失败（{cfg.listen_host}:{cfg.http_port}）: {e}")
            self._http = None
        try:
            self._socks = _TCPServer((cfg.listen_host, cfg.socks5_port), _SocksHandler, self)
        except OSError as e:
            self.log(f"SOCKS5 代理启动失败（{cfg.listen_host}:{cfg.socks5_port}）: {e}")
            self._socks = None
        if not self._http and not self._socks:
            self.log("代理引擎启动失败：无可用监听端口")
            return
        self._refresh_t = threading.Thread(target=self._refresh_loop, daemon=True)
        self._refresh_t.start()
        for srv in (self._http, self._socks):
            if srv:
                threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.log("代理引擎已就绪")

    def stop(self) -> None:
        self._stopping = True
        for srv in (self._http, self._socks):
            if srv:
                try:
                    srv.shutdown()
                    srv.server_close()
                except Exception:
                    pass
        self._http = None
        self._socks = None
        with self._lock:
            conns = list(self._active.keys())
        for cid in conns:
            self._close_active(cid, "已停止")
        self.log("代理引擎已停止")

    def _refresh_loop(self) -> None:
        while not self._stopping:
            self._detector.refresh()
            self._patch_processes()
            time.sleep(1.0)

    def _patch_processes(self) -> None:
        """后台为尚未解析到进程的活动连接补上进程信息（用于展示与后续规则）。"""
        need_exe = uses_path_rules(self._config_getter())
        with self._lock:
            recs = list(self._active.values())
        for rec in recs:
            if rec.get("pid"):
                continue
            pid, pname, pexe = self._detector.process_for_port(rec.get("src_port", 0), need_exe=need_exe)
            if pid:
                rec["pid"], rec["process"] = pid, pname
                rec["exe"] = pexe

    # ------------------------------------------------------------------ 会话 / 统计
    def _new_session(self, src_port: int) -> dict:
        with self._lock:
            self._next_id += 1
            cid = self._next_id
        rec = {
            "id": cid,
            "ts": time.strftime("%H:%M:%S"),
            "pid": None,
            "process": "",
            "exe": "",
            "dst_host": "",
            "dst_port": 0,
            "src_port": src_port,
            "rule": "",
            "action": "",
            "status": "连接中",
            "up": 0,
            "down": 0,
            "done": False,
            "sock": None,
        }
        with self._lock:
            self._active[cid] = rec
        return rec

    def _route_for(self, rec: dict):
        cfg = self._config_getter()
        ctx = {
            "process": rec["process"],
            "process_exe": rec.get("exe") or "",
            "dest_host": rec["dst_host"],
            "dest_port": rec["dst_port"],
            "src_port": rec["src_port"],
        }
        return pick_route(cfg, ctx)

    def _finalize(self, rec: dict, status: str = "已断开") -> None:
        with self._lock:
            if rec.get("done"):
                return
            rec["done"] = True
            rec["status"] = status
            rec["sock"] = None
            self._active.pop(rec["id"], None)
            self._history.appendleft(dict(rec))
            self._total_up += rec["up"]
            self._total_down += rec["down"]

    def _close_active(self, cid: int, status: str) -> None:
        with self._lock:
            rec = self._active.pop(cid, None)
        if rec:
            sock = rec.get("sock")
            try:
                if sock:
                    sock.close()
            except Exception:
                pass
            rec["status"] = status
            rec["sock"] = None
            self._history.appendleft(dict(rec))

    def _tunnel(self, client, dst, rec: dict) -> None:
        up_c = [0]
        down_c = [0]

        def on_close():
            rec["up"] = rec["up"] + up_c[0]
            rec["down"] = rec["down"] + down_c[0]
            self._finalize(rec, "已断开")

        tunnel(client, dst, up_c, down_c, on_close=on_close)

    def snapshot(self) -> dict:
        with self._lock:
            return {
                "active": list(self._active.values()),
                "history": list(self._history),
                "total_up": self._total_up,
                "total_down": self._total_down,
            }

    def log(self, msg: str) -> None:
        self._log_q.put((time.time(), str(msg)))
        if self._log_cb:
            try:
                self._log_cb(str(msg))
            except Exception:
                pass

    def drain_logs(self) -> List[tuple]:
        out = []
        while True:
            try:
                out.append(self._log_q.get_nowait())
            except queue.Empty:
                break
        return out

    # ------------------------------------------------------------------ 连接处理
    def handle_http(self, client, addr) -> None:
        src_port = addr[1]
        rec = self._new_session(src_port)
        rec["sock"] = client
        _set_nodelay(client)
        cfg = self._config_getter()
        pid, pname, pexe = self._detector.process_for_port(
            src_port, need_exe=uses_path_rules(cfg), allow_scan=needs_process(cfg)
        )
        rec["pid"], rec["process"], rec["exe"] = pid, pname, pexe
        try:
            head, extra = self._read_head(client)
            if not head:
                return
            first, sep, rest = head.partition(b"\r\n")
            line = first.decode("latin-1")
            parts = line.split()
            if len(parts) < 3:
                self._http_error(client, 400, "Bad Request")
                return
            method, target = parts[0].upper(), parts[1]
            version = parts[2]

            if method == "CONNECT":
                host, port = self._split_host_port(target, 443)
                rec["dst_host"], rec["dst_port"] = host, port
                route = self._route_for(rec)
                rec["rule"], rec["action"] = route.rule_name, route.action
                if route.action == "block":
                    self._http_error(client, 403, "Blocked by rules")
                    return
                try:
                    dst = connect_via(route, host, port)
                except Exception as e:
                    self.log(f"CONNECT {host}:{port} 失败（{route.rule_name}）: {e}")
                    self._http_error(client, 502, "Upstream error")
                    return
                try:
                    client.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
                except OSError:
                    dst.close()
                    return
                rec["status"] = "已连接"
                if extra:
                    try:
                        dst.sendall(extra)
                        rec["up"] += len(extra)
                    except OSError:
                        pass
                self._tunnel(client, dst, rec)
                return

            # 普通 HTTP 请求
            if target.startswith("/"):
                self._http_error(client, 400, "请使用代理模式（绝对形式 URL）")
                return
            url = urllib.parse.urlsplit(target)
            host = url.hostname
            if not host:
                self._http_error(client, 400, "Bad URL")
                return
            port = url.port or (443 if url.scheme == "https" else 80)
            path = url.path or "/"
            if url.query:
                path += "?" + url.query
            rec["dst_host"], rec["dst_port"] = host, port
            route = self._route_for(rec)
            rec["rule"], rec["action"] = route.rule_name, route.action
            if route.action == "block":
                self._http_error(client, 403, "Blocked by rules")
                return
            try:
                dst = connect_via(route, host, port)
            except Exception as e:
                self.log(f"HTTP {target} 失败（{route.rule_name}）: {e}")
                self._http_error(client, 502, "Upstream error")
                return
            # 直连 / SOCKS5 上游时改写为目标路径形式；HTTP 上游保留绝对形式
            rewrite = route.action == "direct" or (route.upstream and route.upstream.type == "socks5")
            if rewrite:
                head2 = f"{method} {path} {version}".encode("latin-1") + sep + rest
            else:
                head2 = head
            try:
                dst.sendall(head2 + extra)
            except OSError as e:
                self.log(f"转发请求失败: {e}")
                dst.close()
                return
            rec["up"] += len(head2) + len(extra)
            rec["status"] = "已连接"
            self._tunnel(client, dst, rec)
        except Exception as e:
            self.log(f"HTTP 连接异常（{addr}）: {e}")
        finally:
            self._finalize(rec, "已断开")

    def handle_socks(self, client, addr) -> None:
        src_port = addr[1]
        rec = self._new_session(src_port)
        rec["sock"] = client
        _set_nodelay(client)
        cfg = self._config_getter()
        pid, pname, pexe = self._detector.process_for_port(
            src_port, need_exe=uses_path_rules(cfg), allow_scan=needs_process(cfg)
        )
        rec["pid"], rec["process"], rec["exe"] = pid, pname, pexe
        try:
            client.settimeout(15)
            hdr = self._recv_exact(client, 2)
            if len(hdr) != 2 or hdr[0] != 0x05:
                return
            nmethods = hdr[1]
            if nmethods:
                self._recv_exact(client, nmethods)
            client.sendall(b"\x05\x00")  # 无认证
            req = self._recv_exact(client, 4)
            if len(req) != 4:
                return
            cmd, atyp = req[1], req[3]
            if cmd != 0x01:  # 仅支持 CONNECT
                client.sendall(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
                return
            if atyp == 0x01:
                host = socket.inet_ntoa(self._recv_exact(client, 4))
            elif atyp == 0x03:
                ln = self._recv_exact(client, 1)[0]
                host = self._recv_exact(client, ln).decode("latin-1")
            elif atyp == 0x04:
                host = socket.inet_ntop(socket.AF_INET6, self._recv_exact(client, 16))
            else:
                client.sendall(b"\x05\x08\x00\x01\x00\x00\x00\x00\x00\x00")
                return
            port = int.from_bytes(self._recv_exact(client, 2), "big")
            rec["dst_host"], rec["dst_port"] = host, port
            route = self._route_for(rec)
            rec["rule"], rec["action"] = route.rule_name, route.action
            if route.action == "block":
                client.sendall(b"\x05\x02\x00\x01\x00\x00\x00\x00\x00\x00")  # 不允许
                return
            try:
                dst = connect_via(route, host, port)
            except Exception as e:
                self.log(f"SOCKS5 {host}:{port} 连接失败（{route.rule_name}）: {e}")
                client.sendall(b"\x05\x05\x00\x01\x00\x00\x00\x00\x00\x00")  # 拒绝
                return
            client.sendall(b"\x05\x00\x00\x01\x00\x00\x00\x00\x00\x00")
            rec["status"] = "已连接"
            self._tunnel(client, dst, rec)
        except Exception as e:
            self.log(f"SOCKS5 连接异常（{addr}）: {e}")
        finally:
            self._finalize(rec, "已断开")

    # ------------------------------------------------------------------ 工具方法
    @staticmethod
    def _read_head(sock, timeout: float = 15.0) -> tuple:
        """读取 HTTP 请求头，返回 (head 含终止符, 多余字节)。"""
        sock.settimeout(timeout)
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = sock.recv(4096)
            if not chunk:
                break
            buf += chunk
            if len(buf) > 64 * 1024:
                raise ConnectionError("请求头过大")
        if b"\r\n\r\n" in buf:
            idx = buf.index(b"\r\n\r\n") + 4
            return buf[:idx], buf[idx:]
        return buf, b""

    @staticmethod
    def _recv_exact(sock, n: int, timeout: float = 15.0) -> bytes:
        sock.settimeout(timeout)
        data = b""
        while len(data) < n:
            chunk = sock.recv(n - len(data))
            if not chunk:
                raise ConnectionError("对端关闭")
            data += chunk
        return data

    @staticmethod
    def _split_host_port(target: str, default_port: int) -> tuple:
        """解析 CONNECT 目标 'host:port'，兼容 IPv6 方括号形式。"""
        if target.startswith("["):
            host, _, port = target[1:].partition("]")
            if port.startswith(":"):
                port = port[1:]
            else:
                port = default_port
            return host, int(port)
        if ":" in target:
            host, _, port = target.rpartition(":")
            return host, int(port or default_port)
        return target, default_port

    @staticmethod
    def _http_error(client, code: int, text: str) -> None:
        reasons = {400: "Bad Request", 403: "Forbidden", 407: "Proxy Authentication Required", 502: "Bad Gateway"}
        reason = reasons.get(code, "Error")
        body = f"<h1>{code} {reason}</h1><p>{text}</p>".encode("utf-8")
        resp = (
            f"HTTP/1.1 {code} {reason}\r\n"
            f"Content-Type: text/html; charset=utf-8\r\n"
            f"Content-Length: {len(body)}\r\n"
            f"Connection: close\r\n\r\n"
        ).encode() + body
        try:
            client.sendall(resp)
        except OSError:
            pass
