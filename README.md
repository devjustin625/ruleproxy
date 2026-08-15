# RuleProxy 原生版（C# / .NET 8 / WPF）

这是原 Python 版的 **C# 原生重写**，功能和配置与 Python 版完全兼容，配置仍然保存在
`~/.ruleproxy/config.json`（两个版本可共用同一份配置）。

相比 Python 版的差异与目标：

- **异步 Socket**：`async/await` + `Socket`/`NetworkStream`，高并发下线程开销更低。
- **原生进程识别**：直接调用 `GetExtendedTcpTable`（WinAPI）反查源端口所属进程，不再依赖 psutil。
- **原生系统代理**：直接操作注册表 WinINet 键并广播刷新，无需 winproxy 封装。
- **单文件发布**：`dotnet publish` 自包含单 exe，无需用户安装 .NET 运行时。
- **启动/内存**：WPF 原生 GUI，启动速度和内存占用通常优于 PyQt6。

## 目录结构

```
RuleProxy.Native/
├── RuleProxy.Native.csproj      # 主项目（net8.0-windows，WPF + WinForms 托盘）
├── App.xaml / App.xaml.cs       # 入口：单实例互斥锁、--minimized 启动
├── MainWindow.xaml(.cs)         # 主界面：连接/规则/上游/日志 + 系统代理 + 托盘
└── Core/
    ├── Models.cs                # 配置模型（与 Python JSON 兼容）
    ├── ConfigStore.cs           # 配置加载/保存（~/.ruleproxy/config.json）
    ├── RuleRouter.cs            # 规则匹配与路由决策
    ├── UpstreamClient.cs        # 直连 / HTTP CONNECT / SOCKS5 上游（含重试）
    ├── ProxyEngine.cs           # HTTP + SOCKS5 代理服务端，异步双向转发
    ├── ProcessDetector.cs       # GetExtendedTcpTable 源端口→进程映射
    └── WinProxy.cs              # WinINet 系统代理开关
RuleProxy.Native.Tests/          # 控制台测试（无外部框架依赖）
publish-native.bat               # 一键发布单文件 exe
```

## 构建与运行

需要 .NET 8 SDK。若 `dotnet` 不在 PATH：

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --source winget
```

开发运行：

```powershell
dotnet run --project RuleProxy.Native
```

运行测试（规则引擎 + 端到端 HTTP/CONNECT/SOCKS5 隧道）：

```powershell
dotnet run --project RuleProxy.Native.Tests -c Release
```

发布单文件自包含 exe：

```bat
publish-native.bat
```

产物：`dist\native\RuleProxy.exe`。

## v1.2.8 更新

- 收紧本地代理安全边界，避免系统代理仍开启时停止代理导致流量误导向已关闭端口。
- 增加 DPAPI 上游密码保护、配置损坏备份和更新资产 SHA-256 校验。
- 修复上游握手失败连接泄漏、进程映射陈旧、端口规则性能和连接生命周期问题。
- 增加连接搜索、状态筛选、详情查看、CSV 导出和上游连接测试。
- 完善规则/上游新建编辑、未保存提示、删除确认、失败回滚和托盘状态。
- 统一 WPF 界面颜色、按钮层级、危险操作样式和编辑面板布局。

## 使用

与 Python 版一致：

1. 顶部「启动代理」→ 仅监听本机回环地址 HTTP `127.0.0.1:8888`、SOCKS5 `127.0.0.1:8889`。
2. 「设置系统代理」→ 系统应用流量先进本工具。
3. 「规则」页配置：某进程 / 端口 / 域名 → 直连 / 代理 / 阻止；规则按顺序命中，可上移下移。
4. 「上游代理」页配置 HTTP / SOCKS5 上游（可带账号密码）。
5. 「连接」页实时查看每个连接命中的规则、动作与流量；「日志」页查看引擎日志。
6. 右上角可最小化到系统托盘常驻；在「设置」中启用「开机静默启动」可让开机自启时不显示主窗口。

## 命令行参数

- `--minimized` / `-m`：启动即最小化到托盘（可用于开机自启）。
- 单实例：重复启动会唤醒已运行窗口并退出。

## 已知限制

- 系统代理只对遵循 WinINet 的应用生效（浏览器等）；其余应用请手动填 `127.0.0.1:8888/8889`。
- 客户端侧 SOCKS5 仅支持 CONNECT（TCP），不支持 UDP 中继。
- 为避免意外变成开放代理，当前版本拒绝非回环监听；客户端侧 SOCKS5 只接受 `NO AUTH`，且客户端必须声明支持该方法。
- 规则指定的上游不可用或已停用时会阻止连接，不会静默降级为直连。
- 进程识别依赖系统 TCP 连接表，进程退出后可能短暂显示旧进程名。
- 尚未实现"接管不遵循系统代理的进程"（如需真正 Proxifier 级捕获，需接入 WinDivert/WFP）。
- 上游密码在 Windows 当前用户范围内使用 DPAPI 加密保存；旧版明文配置会在下次保存时自动迁移。
- 自动更新只接受受信 HTTPS 资产并校验 Release 提供的 SHA-256 摘要。
