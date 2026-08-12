# RuleProxy — 分应用 / 分端口代理工具

一个类似 **Proxifier** 的本地代理规则工具（Windows / Python / PyQt6）。
它可以按 **应用进程**、**目标端口**、**目标域名** 设置代理规则，实现例如：

- 目标端口 `80`、`443`（常规网页）→ **直连**
- 目标端口 `8080` → **代理**
- 指定应用（如 `steam.exe`）→ **代理**
- 其余流量 → 默认直连 / 默认代理 / 阻止

## 功能特性

- 🚀 内置 **HTTP 代理** 与 **SOCKS5 代理** 两个监听端口，同时支持二者作为上游代理
- 🧩 四种匹配类型：
  - `应用进程`：按进程名匹配（`chrome` 匹配 `chrome.exe`）；也支持指定 **exe 文件**或**整个文件夹**（文件夹内所有程序都适用），规则里可用「选择文件 / 选择文件夹」直接弹出选择
  - `目标端口`：按目标端口匹配（`8080` / `80,443` / `8000-9000`）
  - `目标主机/域名`：支持通配（`*.example.com`）
  - `客户端源端口`：按本地源端口匹配
- 🎯 三种动作：**直连** / **代理** / **阻止**，规则按顺序命中，支持默认行为
- 📡 实时连接列表：显示每个连接的 PID、进程名、目标、命中规则、上下行流量
- 🪟 一键设置 / 取消 **Windows 系统代理**（WinINet）
- � **开机自启动**（注册表 Run 键，登录后自动运行并最小化到托盘）+ **启动即最小化到托盘** 选项
- �💾 配置自动保存到 `~/.ruleproxy/config.json`，规则修改**即时生效**

## 快速开始

```powershell
# 1. 安装依赖
py -m pip install -r requirements.txt

# 2. 启动
py run.py
```

或直接：

```powershell
py -m pip install PyQt6 psutil
py run.py
```

## 使用流程（分应用代理）

1. 点左上角 **「启动代理」**，本工具在本地监听：
   - HTTP 代理：`127.0.0.1:8888`
   - SOCKS5 代理：`127.0.0.1:8889`
2. 点 **「设置系统代理」**，让系统应用的流量先进入本工具。
3. 在 **「规则」** 页配置：某进程 / 某端口 / 某域名 → 直连 或 代理。
4. 在 **「连接」** 页实时查看每个连接命中了哪条规则、走了哪条通道。

> 例如配置：`8080 → 代理`、`80,443 → 直连`，即可实现“8080 端口走代理，常规端口走直连”。

## 规则示例

| 匹配类型   | 匹配值              | 动作   | 说明                       |
| ---------- | ------------------- | ------ | -------------------------- |
| 目标端口   | `80,443`            | 直连   | 常规网页直连               |
| 目标端口   | `8080`              | 代理   | 指定端口走代理             |
| 目标端口   | `8000-9000`         | 代理   | 端口段走代理               |
| 应用进程   | `steam.exe,origin`  | 代理   | 指定应用走代理             |
| 应用进程   | `D:\Games\`         | 代理   | 整个文件夹内的程序都走代理 |
| 应用进程   | `D:\Games\game.exe` | 代理   | 指定 exe 文件走代理        |
| 目标主机   | `*.google.com`      | 代理   | 指定域名走代理             |
| 应用进程   | `xxx.exe`           | 阻止   | 阻止某个应用联网           |

## 上游代理配置

支持 **HTTP 代理** 与 **SOCKS5 代理**（含用户名密码认证）。
适合搭配 Clash / V2Ray / Shadowsocks 等工具使用，把它们的本地代理端口填进来即可。

## 项目结构

```
ruleproxy/
├── run.py                     # 启动入口
├── requirements.txt
├── proxyapp/
│   ├── app.py                 # 应用引导
│   ├── core/
│   │   ├── config.py          # 配置模型与持久化
│   │   ├── process.py         # 进程检测（源端口→PID→进程名）
│   │   ├── routing.py         # 规则匹配与路由决策
│   │   ├── upstream.py        # 上游代理（HTTP CONNECT / SOCKS5）
│   │   ├── relay.py           # 双向流量转发
│   │   ├── server.py          # 代理引擎（HTTP + SOCKS5 服务端）
│   │   └── winproxy.py        # Windows 系统代理（WinINet）
│   └── gui/
│       ├── main_window.py     # 主窗口（连接/规则/代理/设置/日志）
│       ├── dialogs.py         # 规则与代理编辑对话框
│       └── styles.py          # 白色主题
└── tests/
    ├── test_routing.py        # 规则引擎单元测试
    └── test_integration.py    # 端到端集成测试
```

## 工作原理

```
应用 → 系统代理(127.0.0.1:8888) ──┐
                                 ▼
                        ┌─────────────────┐
                        │  RuleProxy 引擎  │
                        │  1. 源端口反查进程 │
                        │  2. 提取目标主机/端口│
                        │  3. 匹配规则表     │
                        └─────────────────┘
              ┌──────────────┼───────────────┐
              ▼              ▼               ▼
           直连目标       HTTP上游代理      SOCKS5上游代理
```

## 已知限制

- Windows 系统代理（WinINet）只对**遵循系统代理**的应用生效（浏览器等）。
  不遵循系统代理的应用（部分游戏 / 原生程序），请在其设置里手动填写代理地址
  `127.0.0.1:8888`（HTTP）或 `127.0.0.1:8889`（SOCKS5）。
- “按进程识别”依赖系统 TCP 连接表（psutil），进程退出后可能短暂显示旧进程名。
- 当前客户端侧仅支持 SOCKS5 的 CONNECT（TCP）命令，不支持 UDP 中继。

## 打包为单个 exe 与安装程序

项目自带一键打包脚本 `build.bat`（基于 PyInstaller），产物：

- `dist\RuleProxy.exe` —— 单文件、无控制台窗口、带图标的应用 exe
- `dist\RuleProxy-Setup.exe` —— **标准安装程序**（像其他软件一样安装）

```powershell
build.bat
```

### 安装程序（RuleProxy-Setup.exe）

双击运行，可：
- 选择安装位置（默认 `%LOCALAPPDATA%\Programs\RuleProxy`，**无需管理员权限**）
- 创建开始菜单 / 桌面快捷方式
- 在「控制面板 → 程序和功能」里**卸载**（卸载后保留 `~/.ruleproxy` 配置）
- 已安装时再次运行可**覆盖更新**

也可以手动执行：

```powershell
py -m pip install pyinstaller pillow
py tools\make_icon.py
py -m PyInstaller --noconfirm --clean --onefile --windowed --name RuleProxy --icon icon.ico run.py
py -m PyInstaller --noconfirm --clean --onefile --windowed --name RuleProxy-Setup --icon icon.ico `
    --add-data "dist\RuleProxy.exe;." --add-data "icon.ico;." installer\installer_main.py
```

> 生成的 `RuleProxy.exe` 为单文件，可复制到任意电脑运行，无需安装 Python。
> 首次运行解压到临时目录会稍慢（onefile 特性），配置仍保存在 `~/.ruleproxy/config.json`。
> 应用图标由 `source_icon.png` 自动优化生成（去白底、圆角化输出 `icon.ico`）；
> 删除 `source_icon.png` 后回退到程序化绘制的三叉分流图标。

## 测试

```powershell
py -m pytest tests -q
```

覆盖：端口列表解析、域名通配、进程匹配、规则优先级、默认行为，以及
「HTTP 代理 → 上游 HTTP 代理 → 目标服务器」与「SOCKS5 → 直连」的端到端链路。
