"""规则匹配与路由决策。"""
from __future__ import annotations

import fnmatch
import os
from typing import Optional

from .config import AppConfig, Rule, UpstreamConfig


class RouteResult:
    __slots__ = ("action", "upstream", "rule_name")

    def __init__(self, action: str, upstream: Optional[UpstreamConfig] = None, rule_name: str = "默认规则"):
        self.action = action        # direct | proxy | block
        self.upstream = upstream
        self.rule_name = rule_name


def parse_port_set(text: str) -> set:
    """解析端口列表字符串：'80,443' 或 '8000-9000' 或混合。"""
    ports = set()
    for part in (text or "").split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            try:
                a, b = part.split("-", 1)
                ports.update(range(int(a), int(b) + 1))
            except ValueError:
                continue
        else:
            try:
                ports.add(int(part))
            except ValueError:
                continue
    return ports


def host_matches(pattern: str, host: str) -> bool:
    """域名匹配，支持 *.example.com 通配与 * 任意。"""
    pat = pattern.strip().lower().rstrip(".")
    h = (host or "").lower().rstrip(".")
    if not pat or not h:
        return False
    if pat == "*":
        return True
    if pat.startswith("*."):
        base = pat[2:]
        return h == base or h.endswith("." + base)
    if "*" in pat:
        return fnmatch.fnmatchcase(h, pat)
    return h == pat


def _norm_path(p: str) -> str:
    """路径归一化：统一分隔符与小写，便于比较。"""
    return (p or "").replace("/", "\\").lower()


def _is_path_value(v: str) -> bool:
    """判断匹配值是否为路径/文件夹形式（而非纯进程名）。"""
    return "\\" in v or "/" in v or os.path.isabs(v)


def _match_process_item(item: str, name: str, exe: str) -> bool:
    """匹配单个“应用进程”规则项。

    - 纯进程名：chrome → 匹配 chrome.exe
    - 文件路径：C:\\Games\\game.exe → 精确匹配该 exe
    - 文件夹：C:\\Games\\ 或 C:\\Games → 文件夹内所有程序
    - 通配符：C:\\Games\\*.exe
    """
    v = item.strip()
    if not v:
        return False
    if _is_path_value(v):
        if not exe:
            return False
        low_exe = _norm_path(exe)
        if "*" in v or "?" in v:
            return fnmatch.fnmatch(low_exe, _norm_path(v))
        if v.endswith(("\\", "/")) or os.path.isdir(v):
            base = _norm_path(v.rstrip("\\/"))
            return low_exe == base or low_exe.startswith(base + "\\")
        return low_exe == _norm_path(v)
    pn = (name or "").lower()
    if not pn:
        return False
    n = v.lower()
    return pn == n or (len(n) >= 2 and pn.startswith(n))


def uses_path_rules(cfg: AppConfig) -> bool:
    """是否存在需要解析进程 exe 路径的规则（用于按需获取进程路径）。"""
    for rule in cfg.rules:
        if rule.enabled and rule.match_type == "process":
            for item in (rule.match_value or "").split(","):
                if item.strip() and _is_path_value(item.strip()):
                    return True
    return False


def needs_process(cfg: AppConfig) -> bool:
    """是否存在启用中的“应用进程”规则（决定连接热路径是否解析进程）。"""
    for rule in cfg.rules:
        if rule.enabled and rule.match_type == "process":
            return True
    return False


def match_rule(rule: Rule, ctx: dict) -> bool:
    """判断规则是否命中。ctx: process/process_exe/dest_host/dest_port/src_port。"""
    if not rule.enabled:
        return False
    mt = rule.match_type
    val = rule.match_value or ""
    if mt == "process":
        names = [n for n in (val or "").split(",") if n.strip()]
        if not names:
            return False
        return any(
            _match_process_item(n, ctx.get("process", ""), ctx.get("process_exe", ""))
            for n in names
        )
    if mt == "dest_port":
        return ctx.get("dest_port") in parse_port_set(val)
    if mt == "src_port":
        return ctx.get("src_port") in parse_port_set(val)
    if mt == "dest_host":
        return any(host_matches(pat, ctx.get("dest_host", "")) for pat in val.split(","))
    return False


def find_upstream(cfg: AppConfig, name: str) -> Optional[UpstreamConfig]:
    """按名字找上游代理；名字为空或找不到时回退到第一个启用项。"""
    if name:
        for p in cfg.proxies:
            if p.name == name and p.enabled:
                return p
        for p in cfg.proxies:
            if p.name == name:
                return p
    for p in cfg.proxies:
        if p.enabled:
            return p
    return None


def pick_route(cfg: AppConfig, ctx: dict) -> RouteResult:
    """按规则顺序（第一条命中的生效）决定路由。"""
    for rule in cfg.rules:
        if match_rule(rule, ctx):
            if rule.action == "block":
                return RouteResult("block", rule_name=rule.name)
            if rule.action == "direct":
                return RouteResult("direct", rule_name=rule.name)
            up = find_upstream(cfg, rule.proxy)
            if up is None:
                return RouteResult("direct", rule_name=rule.name + "（无可用代理→直连）")
            return RouteResult("proxy", up, rule.name)

    default = cfg.default_action or "direct"
    if default == "block":
        return RouteResult("block", rule_name="默认规则")
    if default == "proxy":
        up = find_upstream(cfg, cfg.default_proxy)
        if up is None:
            return RouteResult("direct", rule_name="默认规则（无可用代理→直连）")
        return RouteResult("proxy", up, "默认规则")
    return RouteResult("direct", rule_name="默认规则")
