"""端到端集成测试：真实 TCP 隧道 + 假上游 HTTP 代理 + 假目标服务器。"""
import os
import socket
import struct
import sys
import threading

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from proxyapp.core.config import AppConfig, Rule, UpstreamConfig  # noqa: E402
from proxyapp.core.server import ProxyEngine  # noqa: E402


def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def recv_exact(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError("closed")
        data += chunk
    return data


class FakeTarget(threading.Thread):
    """回显服务器：收到什么返回 ECHO:什么。"""

    def __init__(self):
        super().__init__(daemon=True)
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", 0))
        self.srv.listen(5)
        self.port = self.srv.getsockname()[1]

    def run(self):
        while True:
            try:
                c, _ = self.srv.accept()
            except OSError:
                return
            threading.Thread(target=self._serve_conn, args=(c,), daemon=True).start()

    def _serve_conn(self, c):
        try:
            while True:
                d = c.recv(4096)
                if not d:
                    break
                c.sendall(b"ECHO:" + d)
        except OSError:
            pass
        finally:
            try:
                c.close()
            except OSError:
                pass

    def close(self):
        try:
            self.srv.close()
        except OSError:
            pass


class FakeHttpUpstream(threading.Thread):
    """模拟 HTTP 代理：处理 CONNECT，返回 200 后双向转发。"""

    def __init__(self):
        super().__init__(daemon=True)
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", 0))
        self.srv.listen(5)
        self.port = self.srv.getsockname()[1]
        self.connects = []
        self._stop = False

    def run(self):
        while not self._stop:
            try:
                c, _ = self.srv.accept()
            except OSError:
                return
            threading.Thread(target=self._serve_conn, args=(c,), daemon=True).start()

    def _serve_conn(self, client):
        try:
            data = b""
            while b"\r\n\r\n" not in data:
                chunk = client.recv(4096)
                if not chunk:
                    return
                data += chunk
            line = data.split(b"\r\n", 1)[0].decode()
            parts = line.split()
            if not parts or parts[0] != "CONNECT":
                client.sendall(b"HTTP/1.1 400 Bad Request\r\n\r\n")
                return
            host, _, port_s = parts[1].rpartition(":")
            self.connects.append((host, int(port_s)))
            up = socket.create_connection((host, int(port_s)), 5)
            client.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
            stop = threading.Event()

            def pump(a, b):
                try:
                    while not stop.is_set():
                        d = a.recv(65536)
                        if not d:
                            break
                        b.sendall(d)
                except OSError:
                    pass
                finally:
                    stop.set()

            t1 = threading.Thread(target=pump, args=(client, up), daemon=True)
            t2 = threading.Thread(target=pump, args=(up, client), daemon=True)
            t1.start()
            t2.start()
            t1.join()
            t2.join()
            for s in (client, up):
                try:
                    s.close()
                except OSError:
                    pass
        except OSError:
            pass

    def close(self):
        self._stop = True
        try:
            self.srv.close()
        except OSError:
            pass


def _make_engine(cfg):
    engine = ProxyEngine(lambda: cfg)
    engine.start()
    assert engine.running
    return engine


def test_proxy_route_through_http_upstream():
    target = FakeTarget()
    target.start()
    up = FakeHttpUpstream()
    up.start()
    cfg = AppConfig(listen_host="127.0.0.1", http_port=free_port(), socks5_port=free_port())
    cfg.proxies = [UpstreamConfig(name="up", type="http", host="127.0.0.1", port=up.port)]
    cfg.rules = [
        Rule(name="to_proxy", match_type="dest_port", match_value=str(target.port), action="proxy", proxy="up")
    ]
    engine = _make_engine(cfg)
    try:
        s = socket.create_connection(("127.0.0.1", cfg.http_port), 5)
        s.sendall(
            f"CONNECT 127.0.0.1:{target.port} HTTP/1.1\r\nHost: 127.0.0.1:{target.port}\r\n\r\n".encode()
        )
        line = s.recv(4096).split(b"\r\n", 1)[0]
        assert b"200" in line, line
        s.sendall(b"hello")
        assert recv_exact(s, 10) == b"ECHO:hello"
        s.close()
        # 确认请求确实经过了上游代理
        assert ("127.0.0.1", target.port) in up.connects
    finally:
        engine.stop()
        target.close()
        up.close()


def test_direct_route():
    target = FakeTarget()
    target.start()
    cfg = AppConfig(listen_host="127.0.0.1", http_port=free_port(), socks5_port=free_port())
    cfg.proxies = [UpstreamConfig(name="up", type="http", host="127.0.0.1", port=1)]  # 不会被用到
    cfg.rules = [
        Rule(name="direct", match_type="dest_port", match_value=str(target.port), action="direct")
    ]
    engine = _make_engine(cfg)
    try:
        s = socket.create_connection(("127.0.0.1", cfg.http_port), 5)
        s.sendall(
            f"CONNECT 127.0.0.1:{target.port} HTTP/1.1\r\nHost: x\r\n\r\n".encode()
        )
        line = s.recv(4096).split(b"\r\n", 1)[0]
        assert b"200" in line, line
        s.sendall(b"ping")
        assert recv_exact(s, 9) == b"ECHO:ping"
        s.close()
    finally:
        engine.stop()
        target.close()


def test_block_route():
    target = FakeTarget()
    target.start()
    cfg = AppConfig(listen_host="127.0.0.1", http_port=free_port(), socks5_port=free_port())
    cfg.rules = [
        Rule(name="block", match_type="dest_port", match_value=str(target.port), action="block")
    ]
    engine = _make_engine(cfg)
    try:
        s = socket.create_connection(("127.0.0.1", cfg.http_port), 5)
        s.sendall(
            f"CONNECT 127.0.0.1:{target.port} HTTP/1.1\r\nHost: x\r\n\r\n".encode()
        )
        line = s.recv(4096).split(b"\r\n", 1)[0]
        assert b"403" in line, line
        s.close()
    finally:
        engine.stop()
        target.close()


def test_socks5_client_direct():
    target = FakeTarget()
    target.start()
    cfg = AppConfig(listen_host="127.0.0.1", http_port=free_port(), socks5_port=free_port())
    cfg.rules = [
        Rule(name="direct", match_type="dest_port", match_value=str(target.port), action="direct")
    ]
    engine = _make_engine(cfg)
    try:
        s = socket.create_connection(("127.0.0.1", cfg.socks5_port), 5)
        s.sendall(b"\x05\x01\x00")
        assert recv_exact(s, 2) == b"\x05\x00"
        req = b"\x05\x01\x00\x01" + socket.inet_aton("127.0.0.1") + struct.pack(">H", target.port)
        s.sendall(req)
        reply = recv_exact(s, 10)
        assert reply[1] == 0x00, reply
        s.sendall(b"hey")
        assert recv_exact(s, 8) == b"ECHO:hey"
        s.close()
    finally:
        engine.stop()
        target.close()


def test_process_rule_routing():
    """进程规则：按进程名匹配应正确路由（防止进程识别回归）。

    测试进程是 python.exe（pytest），规则应命中并走上游代理，
    而不是因识别不到进程而回退直连。
    """
    target = FakeTarget()
    target.start()
    up = FakeHttpUpstream()
    up.start()
    cfg = AppConfig(listen_host="127.0.0.1", http_port=free_port(), socks5_port=free_port())
    cfg.proxies = [UpstreamConfig(name="up", type="http", host="127.0.0.1", port=up.port)]
    cfg.rules = [
        Rule(name="py_proxy", match_type="process", match_value="python.exe", action="proxy", proxy="up")
    ]
    engine = _make_engine(cfg)
    try:
        s = socket.create_connection(("127.0.0.1", cfg.http_port), 5)
        s.sendall(f"CONNECT 127.0.0.1:{target.port} HTTP/1.1\r\nHost: x\r\n\r\n".encode())
        line = s.recv(4096).split(b"\r\n", 1)[0]
        assert b"200" in line, line
        s.sendall(b"ping")
        assert recv_exact(s, 9) == b"ECHO:ping"
        s.close()
        # 应经过上游代理（进程规则命中，而非直连）
        assert ("127.0.0.1", target.port) in up.connects, up.connects
    finally:
        engine.stop()
        target.close()
        up.close()


class FlakyUpstream(threading.Thread):
    """上游代理：前 fail_count 个连接直接断开（模拟 FLClash 切换/重启），之后正常。"""

    def __init__(self, fail_count: int):
        super().__init__(daemon=True)
        self.fail_count = fail_count
        self.connects = []
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", 0))
        self.srv.listen(16)
        self.port = self.srv.getsockname()[1]

    def run(self):
        while True:
            try:
                c, _ = self.srv.accept()
            except OSError:
                return
            if self.fail_count > 0:
                self.fail_count -= 1
                c.close()  # 直接断开，模拟上游瞬时不可用
                continue
            threading.Thread(target=self._serve, args=(c,), daemon=True).start()

    def _serve(self, client):
        try:
            data = b""
            while b"\r\n\r\n" not in data:
                chunk = client.recv(4096)
                if not chunk:
                    return
                data += chunk
            line = data.split(b"\r\n", 1)[0].decode()
            parts = line.split()
            if not parts or parts[0] != "CONNECT":
                client.sendall(b"HTTP/1.1 400 Bad Request\r\n\r\n")
                return
            host, _, port_s = parts[1].rpartition(":")
            self.connects.append((host, int(port_s)))
            up = socket.create_connection((host, int(port_s)), 5)
            client.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
            stop = threading.Event()

            def pump(a, b):
                try:
                    while not stop.is_set():
                        d = a.recv(65536)
                        if not d:
                            break
                        b.sendall(d)
                except OSError:
                    pass
                finally:
                    stop.set()

            t1 = threading.Thread(target=pump, args=(client, up), daemon=True)
            t2 = threading.Thread(target=pump, args=(up, client), daemon=True)
            t1.start()
            t2.start()
            t1.join()
            t2.join()
            for s in (client, up):
                try:
                    s.close()
                except OSError:
                    pass
        except OSError:
            pass

    def close(self):
        try:
            self.srv.close()
        except OSError:
            pass


def test_upstream_retry_after_flaky():
    """上游（FLClash）切换/重启导致的瞬时失败应自动重试，不让应用失效。"""
    target = FakeTarget()
    target.start()
    up = FlakyUpstream(fail_count=2)  # 前 2 次连接直接断开
    up.start()
    cfg = AppConfig(listen_host="127.0.0.1", http_port=free_port(), socks5_port=free_port())
    cfg.proxies = [UpstreamConfig(name="up", type="http", host="127.0.0.1", port=up.port)]
    cfg.rules = [
        Rule(name="p", match_type="dest_port", match_value=str(target.port), action="proxy", proxy="up")
    ]
    engine = _make_engine(cfg)
    try:
        s = socket.create_connection(("127.0.0.1", cfg.http_port), 5)
        s.settimeout(8)
        s.sendall(f"CONNECT 127.0.0.1:{target.port} HTTP/1.1\r\nHost: x\r\n\r\n".encode())
        line = s.recv(4096).split(b"\r\n", 1)[0]
        assert b"200" in line, line
        s.sendall(b"ping")
        assert recv_exact(s, 9) == b"ECHO:ping"
        s.close()
        assert ("127.0.0.1", target.port) in up.connects, up.connects
    finally:
        engine.stop()
        target.close()
        up.close()
