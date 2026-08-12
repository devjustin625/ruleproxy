"""配置模型与持久化。"""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from typing import List

APP_NAME = "RuleProxy"
APP_VERSION = "0.1.0"
CONFIG_DIR = os.path.join(os.path.expanduser("~"), ".ruleproxy")
CONFIG_PATH = os.path.join(CONFIG_DIR, "config.json")


@dataclass
class UpstreamConfig:
    """上游代理配置（HTTP / SOCKS5）。"""
    name: str = "默认代理"
    type: str = "http"          # http | socks5
    host: str = "127.0.0.1"
    port: int = 7890
    username: str = ""
    password: str = ""
    enabled: bool = True


@dataclass
class Rule:
    """代理规则。

    match_type:
      - process   应用进程（按进程名匹配，如 chrome 匹配 chrome.exe）
      - dest_port 目标端口（支持 80,443 或 8000-9000）
      - dest_host 目标主机 / 域名（支持 *.example.com 通配）
      - src_port  客户端源端口
    action:
      - direct 直连
      - proxy  走代理（proxy 字段指定上游代理名，空则自动选第一个可用）
      - block  阻止
    """
    name: str = "新规则"
    enabled: bool = True
    match_type: str = "dest_port"
    match_value: str = "8080"
    action: str = "proxy"       # direct | proxy | block
    proxy: str = ""             # 上游代理名
    note: str = ""


@dataclass
class AppConfig:
    listen_host: str = "127.0.0.1"
    http_port: int = 8888
    socks5_port: int = 8889
    default_action: str = "direct"      # direct | proxy | block（未命中规则时的默认行为）
    default_proxy: str = ""
    start_minimized: bool = False       # 启动时最小化到托盘
    rules: List[Rule] = field(default_factory=list)
    proxies: List[UpstreamConfig] = field(default_factory=list)


def _default_config() -> AppConfig:
    cfg = AppConfig()
    cfg.proxies = [
        UpstreamConfig(name="我的代理", type="http", host="127.0.0.1", port=7890, enabled=True),
    ]
    cfg.rules = [
        Rule(name="常规端口直连", enabled=True, match_type="dest_port",
             match_value="80,443", action="direct", note="80/443 端口走直连"),
        Rule(name="8080 代理", enabled=True, match_type="dest_port",
             match_value="8080", action="proxy", proxy="我的代理", note="8080 端口走代理"),
        Rule(name="指定应用代理", enabled=False, match_type="process",
             match_value="steam.exe,origin.exe", action="proxy", proxy="我的代理", note="按应用名匹配"),
    ]
    return cfg


def load_config() -> AppConfig:
    """加载配置；不存在或损坏时生成默认配置。"""
    if not os.path.exists(CONFIG_PATH):
        cfg = _default_config()
        save_config(cfg)
        return cfg
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        cfg = AppConfig(
            listen_host=data.get("listen_host", "127.0.0.1"),
            http_port=int(data.get("http_port", 8888)),
            socks5_port=int(data.get("socks5_port", 8889)),
            default_action=data.get("default_action", "direct"),
            default_proxy=data.get("default_proxy", ""),
            start_minimized=data.get("start_minimized", False),
            rules=[Rule(**r) for r in data.get("rules", [])],
            proxies=[UpstreamConfig(**p) for p in data.get("proxies", [])],
        )
        return cfg
    except Exception:
        cfg = _default_config()
        save_config(cfg)
        return cfg


def save_config(cfg: AppConfig) -> None:
    """保存配置到磁盘。"""
    os.makedirs(CONFIG_DIR, exist_ok=True)
    data = {
        "listen_host": cfg.listen_host,
        "http_port": cfg.http_port,
        "socks5_port": cfg.socks5_port,
        "default_action": cfg.default_action,
        "default_proxy": cfg.default_proxy,
        "start_minimized": cfg.start_minimized,
        "rules": [r.__dict__ for r in cfg.rules],
        "proxies": [p.__dict__ for p in cfg.proxies],
    }
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
