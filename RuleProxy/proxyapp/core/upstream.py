"""上游代理连接：HTTP CONNECT 与 SOCKS5 协议客户端。"""
from __future__ import annotations

import base64
import socket
import struct
import threading
import time

from .config import UpstreamConfig


class UpstreamError(Exception):
    pass


def _read_until(sock, marker=b"\r\n\r\n", cap=64 * 1024) -> bytes:
    data = b""
    while marker not in data:
        chunk = sock.recv(4096)
        if not chunk:
            raise UpstreamError("上游代理提前关闭连接")
        data += chunk
        if len(data) > cap:
            raise UpstreamError("上游代理响应头过大")
    return data


def connect_http(proxy: UpstreamConfig, host: str, port: int, timeout: float = 12.0) -> socket.socket:
    """通过 HTTP 代理建立到 host:port 的 CONNECT 隧道。"""
    sock = socket.create_connection((proxy.host, proxy.port), timeout)
    sock.settimeout(timeout)
    target = f"{host}:{port}"
    req = f"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n"
    if proxy.username:
        token = base64.b64encode(f"{proxy.username}:{proxy.password}".encode()).decode()
        req += f"Proxy-Authorization: Basic {token}\r\n"
    req += "\r\n"
    sock.sendall(req.encode())
    resp = _read_until(sock)
    try:
        code = int(resp.split(b"\r\n", 1)[0].split()[1])
    except Exception:
        raise UpstreamError(f"无法解析上游代理响应: {resp[:120]!r}")
    if code != 200:
        raise UpstreamError(f"上游代理 CONNECT 失败: HTTP {code}")
    sock.settimeout(None)
    return sock


def _socks_request_bytes(host: str, port: int) -> bytes:
    """构造 SOCKS5 CONNECT 请求（ATYP 自动选择 IPv4 / 域名 / IPv6）。"""
    try:
        ip = socket.inet_aton(host)
        return b"\x05\x01\x00\x01" + ip + struct.pack(">H", port)
    except OSError:
        pass
    try:
        ip6 = socket.inet_pton(socket.AF_INET6, host)
        return b"\x05\x01\x00\x04" + ip6 + struct.pack(">H", port)
    except OSError:
        pass
    hostb = host.encode("idna")
    if len(hostb) > 255:
        raise UpstreamError("主机名过长")
    return b"\x05\x01\x00\x03" + bytes([len(hostb)]) + hostb + struct.pack(">H", port)


def _recv_exact(sock: socket.socket, n: int) -> bytes:
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise UpstreamError("SOCKS5 上游提前关闭")
        data += chunk
    return data


def connect_socks5(proxy: UpstreamConfig, host: str, port: int, timeout: float = 12.0) -> socket.socket:
    """通过 SOCKS5 代理建立到 host:port 的连接。"""
    sock = socket.create_connection((proxy.host, proxy.port), timeout)
    sock.settimeout(timeout)

    if proxy.username:
        # 用户名 / 密码认证 (RFC 1929)
        u = proxy.username.encode()
        p = proxy.password.encode()
        sock.sendall(b"\x05\x02\x00\x02")
        ver = _recv_exact(sock, 2)
        if ver != b"\x05\x02":
            raise UpstreamError("SOCKS5 上游不支持用户名密码认证")
        body = bytes([len(u)]) + u + bytes([len(p)]) + p
        sock.sendall(b"\x01" + body)
        status = _recv_exact(sock, 2)
        if status[1] != 0x00:
            raise UpstreamError("SOCKS5 认证失败")
    else:
        sock.sendall(b"\x05\x01\x00")
        ver = _recv_exact(sock, 2)
        if ver != b"\x05\x00":
            raise UpstreamError("SOCKS5 上游要求认证或握手失败")

    sock.sendall(_socks_request_bytes(host, port))
    head = _recv_exact(sock, 4)
    if head[0] != 0x05:
        raise UpstreamError("SOCKS5 上游响应版本错误")
    if head[1] != 0x00:
        raise UpstreamError(f"SOCKS5 上游连接失败: REP={head[1]}")
    atyp = head[3]
    if atyp == 0x01:
        _recv_exact(sock, 4)
    elif atyp == 0x04:
        _recv_exact(sock, 16)
    elif atyp == 0x03:
        ln = _recv_exact(sock, 1)[0]
        _recv_exact(sock, ln)
    _recv_exact(sock, 2)  # 端口
    sock.settimeout(None)
    return sock


# ---- 直连 DNS 缓存：避免每个连接都重复解析域名，拖慢网页打开 ----
_DNS_CACHE: dict = {}
_DNS_LOCK = threading.Lock()
_DNS_TTL = 60.0


def _is_ip(host: str) -> bool:
    for af in (socket.AF_INET, socket.AF_INET6):
        try:
            socket.inet_pton(af, host)
            return True
        except OSError:
            continue
    return False


def _addrinfo_cached(host: str):
    """解析域名并缓存地址列表（60s TTL），命中缓存时零网络开销。"""
    now = time.time()
    with _DNS_LOCK:
        entry = _DNS_CACHE.get(host)
        if entry and now - entry[0] < _DNS_TTL:
            return entry[1]
    try:
        infos = socket.getaddrinfo(host, None, proto=socket.IPPROTO_TCP)
    except OSError:
        raise UpstreamError(f"无法解析域名: {host}")
    addrs = [info[4] for info in infos]
    with _DNS_LOCK:
        _DNS_CACHE[host] = (time.time(), addrs)
        if len(_DNS_CACHE) > 512:
            _DNS_CACHE.clear()
    return addrs


def connect_direct(host: str, port: int, timeout: float = 12.0) -> socket.socket:
    """直连目标主机（IP 直连 / 域名走 DNS 缓存，多地址逐个尝试）。

    逐个尝试解析出的所有地址（IPv4/IPv6），与系统 create_connection 行为一致，
    避免仅取首个地址（可能是不通的双栈 IPv6）导致连接失败。
    """
    if _is_ip(host):
        addrs = [(host, port)]
    else:
        addrs = [(sa[0], port) for sa in _addrinfo_cached(host)]
    last = None
    for addr in addrs:
        try:
            sock = socket.create_connection(addr, timeout)
            sock.settimeout(None)
            return sock
        except OSError as e:
            last = e
    if last:
        raise last
    raise UpstreamError(f"无法连接: {host}:{port}")


def connect_via(route, host: str, port: int, timeout: float = 12.0) -> socket.socket:
    """根据路由结果建立到目标主机/端口的连接。route.action: direct | proxy"""
    if route.action == "direct":
        return connect_direct(host, port, timeout)
    up = route.upstream
    if up is None:
        raise UpstreamError("未配置可用的上游代理")
    if up.type == "socks5":
        return connect_socks5(up, host, port, timeout)
    return connect_http(up, host, port, timeout)
