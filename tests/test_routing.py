"""规则引擎单元测试。"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from proxyapp.core.config import AppConfig, Rule, UpstreamConfig  # noqa: E402
from proxyapp.core.routing import (  # noqa: E402
    host_matches,
    match_rule,
    needs_process,
    parse_port_set,
    pick_route,
    uses_path_rules,
)


def test_parse_port_set():
    assert 80 in parse_port_set("80,443")
    assert 443 in parse_port_set("80, 443")
    assert 8080 in parse_port_set("8000-9000")
    assert 8000 in parse_port_set("8000-9000")
    assert 9000 in parse_port_set("8000-9000")
    assert 9001 not in parse_port_set("8000-9000")
    assert parse_port_set("abc") == set()


def test_host_matches():
    assert host_matches("example.com", "example.com")
    assert host_matches("*.google.com", "www.google.com")
    assert host_matches("*.google.com", "google.com")
    assert not host_matches("*.google.com", "google.org")
    assert host_matches("*", "anything.com")
    assert not host_matches("example.com", "")


def test_match_process():
    r = Rule(name="p", match_type="process", match_value="chrome")
    assert match_rule(r, {"process": "chrome.exe"})
    assert match_rule(r, {"process": "Chrome.exe"})
    assert not match_rule(r, {"process": "firefox.exe"})
    assert not match_rule(r, {"process": ""})


def test_match_dest_port():
    r = Rule(name="p", match_type="dest_port", match_value="8080")
    assert match_rule(r, {"dest_port": 8080})
    assert not match_rule(r, {"dest_port": 80})


def test_disabled_rule_not_matched():
    r = Rule(name="p", match_type="dest_port", match_value="8080", enabled=False)
    assert not match_rule(r, {"dest_port": 8080})


def test_pick_route_priority():
    cfg = AppConfig()
    cfg.proxies = [UpstreamConfig(name="p1", type="http", host="127.0.0.1", port=7890)]
    cfg.rules = [
        Rule(name="direct80", match_type="dest_port", match_value="80", action="direct"),
        Rule(name="proxy8080", match_type="dest_port", match_value="8080", action="proxy", proxy="p1"),
    ]
    r1 = pick_route(cfg, {"dest_port": 80})
    assert r1.action == "direct"
    assert r1.rule_name == "direct80"

    r2 = pick_route(cfg, {"dest_port": 8080})
    assert r2.action == "proxy"
    assert r2.upstream.name == "p1"

    # 未命中 → 默认直连
    r3 = pick_route(cfg, {"dest_port": 443})
    assert r3.action == "direct"
    assert r3.rule_name == "默认规则"


def test_pick_route_block_first_wins():
    cfg = AppConfig()
    cfg.proxies = [UpstreamConfig(name="p1", type="http", host="127.0.0.1", port=7890)]
    cfg.rules = [
        Rule(name="block_all_high", match_type="dest_port", match_value="8000-9999", action="block"),
        Rule(name="proxy8080", match_type="dest_port", match_value="8080", action="proxy", proxy="p1"),
    ]
    r = pick_route(cfg, {"dest_port": 8080})
    assert r.action == "block"


def test_pick_route_no_proxy_fallback_direct():
    cfg = AppConfig()
    cfg.rules = [
        Rule(name="proxy_rule", match_type="dest_port", match_value="8080", action="proxy", proxy="none"),
    ]
    r = pick_route(cfg, {"dest_port": 8080})
    assert r.action == "direct"


def test_pick_route_default_proxy():
    cfg = AppConfig(default_action="proxy", default_proxy="p2")
    cfg.proxies = [UpstreamConfig(name="p1", type="http", host="127.0.0.1", port=7890, enabled=False),
                   UpstreamConfig(name="p2", type="socks5", host="127.0.0.1", port=1080)]
    r = pick_route(cfg, {"dest_port": 22})
    assert r.action == "proxy"
    assert r.upstream.name == "p2"


def test_match_process_name_exe_suffix():
    # 纯 exe 名（无路径）仍按进程名匹配
    r = Rule(name="n", match_type="process", match_value="chrome.exe")
    assert match_rule(r, {"process": "chrome.exe",
                          "process_exe": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"})
    assert not match_rule(r, {"process": "firefox.exe"})


def test_match_process_path_folder_trailing():
    r = Rule(name="folder", match_type="process", match_value="C:\\Games\\")
    assert match_rule(r, {"process": "game.exe", "process_exe": "c:\\Games\\game.exe"})
    assert match_rule(r, {"process": "sub.exe", "process_exe": "C:\\Games\\sub\\sub.exe"})
    assert not match_rule(r, {"process": "x.exe", "process_exe": "D:\\Other\\x.exe"})


def test_match_process_path_file_exact():
    r = Rule(name="file", match_type="process", match_value="C:\\Games\\game.exe")
    assert match_rule(r, {"process": "game.exe", "process_exe": "c:\\games\\game.exe"})
    assert not match_rule(r, {"process": "game.exe", "process_exe": "C:\\Games\\other.exe"})


def test_match_process_path_wildcard():
    r = Rule(name="wc", match_type="process", match_value="C:\\Games\\*.exe")
    assert match_rule(r, {"process": "game.exe", "process_exe": "C:\\Games\\game.exe"})
    assert not match_rule(r, {"process": "game.exe", "process_exe": "C:\\Other\\game.exe"})


def test_match_process_path_missing_exe():
    # 未解析到 exe 路径时，路径类规则不命中
    r = Rule(name="f", match_type="process", match_value="C:\\Games\\")
    assert not match_rule(r, {"process": "game.exe", "process_exe": ""})


def test_uses_path_rules():
    cfg = AppConfig()
    cfg.rules = [Rule(name="a", match_type="process", match_value="chrome")]
    assert not uses_path_rules(cfg)
    cfg.rules = [Rule(name="b", match_type="process", match_value="C:\\Games\\")]
    assert uses_path_rules(cfg)


def test_needs_process():
    cfg = AppConfig()
    cfg.rules = [Rule(name="a", match_type="dest_port", match_value="80", action="direct")]
    assert not needs_process(cfg)
    cfg.rules.append(Rule(name="b", match_type="process", match_value="chrome"))
    assert needs_process(cfg)
    cfg.rules[-1].enabled = False
    assert not needs_process(cfg)


def test_is_ip_detection():
    from proxyapp.core.upstream import _is_ip
    assert _is_ip("127.0.0.1")
    assert _is_ip("::1")
    assert _is_ip("192.168.1.1")
    assert not _is_ip("example.com")
    assert not _is_ip("localhost")


def test_autostart_command():
    from proxyapp.core import autostart
    cmd = autostart._command()
    assert "--minimized" in cmd
    assert "run.py" in cmd  # 源码模式下指向 python + run.py
