using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RuleProxy.Native.Core;

var failures = 0;
void Check(string name, bool condition)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    if (!condition) failures++;
}

// ---- 1. 配置序列化往返 ----
var config = new AppConfig { DefaultAction = "direct" };
config.Proxies.Add(new UpstreamConfig { Name = "p1", Type = "http", Host = "127.0.0.1", Port = 7890, Enabled = true });
config.Rules.Add(new ProxyRule { Name = "r1", MatchType = "dest_port", MatchValue = "8080", Action = "proxy", Proxy = "p1" });
var json = JsonSerializer.Serialize(config);
var reloaded = JsonSerializer.Deserialize<AppConfig>(json)!;
Check("config 序列化往返", reloaded.Rules[0].MatchValue == "8080" && reloaded.Proxies[0].Name == "p1" && reloaded.HttpPort == 8888);

// ---- 2. 端口解析 ----
var ports = RuleRouter.ParsePorts("80,443");
Check("端口列表解析", ports.Contains(80) && ports.Contains(443) && !ports.Contains(8080));
var range = RuleRouter.ParsePorts("8000-8002");
Check("端口段解析", range.Contains(8000) && range.Contains(8001) && range.Contains(8002) && range.Count == 3);

// ---- 3. 域名通配 ----
Check("域名通配匹配", RuleRouter.HostMatches("*.example.com", "www.example.com"));
Check("域名通配根域", RuleRouter.HostMatches("*.example.com", "example.com"));
Check("域名通配反例", !RuleRouter.HostMatches("*.example.com", "badexample.com"));
Check("域名精确匹配", RuleRouter.HostMatches("google.com", "google.com"));

// ---- 4. HTTP 监听状态 ----
var occupiedHttpListener = new TcpListener(IPAddress.Loopback, 0);
occupiedHttpListener.Start();
var occupiedHttpPort = ((IPEndPoint)occupiedHttpListener.LocalEndpoint).Port;
var partialEngineConfig = new AppConfig
{
    ListenHost = "127.0.0.1",
    HttpPort = occupiedHttpPort,
    Socks5Port = FreePort()
};
var partialEngine = new ProxyEngine(() => partialEngineConfig);
partialEngine.Start();
Check("HTTP 端口被占用时不报告 HTTP 已监听", partialEngine.Running && !partialEngine.HttpListening);
partialEngine.Stop();
occupiedHttpListener.Stop();

// ---- 5. 监听异常退出 ----
var lifecycleEngineConfig = new AppConfig
{
    ListenHost = "127.0.0.1",
    HttpPort = FreePort(),
    Socks5Port = FreePort()
};
var lifecycleEngine = new ProxyEngine(() => lifecycleEngineConfig);
var lifecycleStateChanges = 0;
lifecycleEngine.StateChanged += () => Interlocked.Increment(ref lifecycleStateChanges);
lifecycleEngine.Start();
var httpListenerField = typeof(ProxyEngine).GetField("_httpListener", BindingFlags.Instance | BindingFlags.NonPublic)!;
((TcpListener)httpListenerField.GetValue(lifecycleEngine)!).Stop();
await WaitUntilAsync(() => !lifecycleEngine.HttpListening, TimeSpan.FromSeconds(5));
Check("HTTP 监听异常退出后清理状态", lifecycleEngine.Running && !lifecycleEngine.HttpListening);
Check("HTTP 监听异常退出触发状态变更", Volatile.Read(ref lifecycleStateChanges) >= 2);
lifecycleEngine.Stop();

// ---- 6. 规则路由 ----
var direct = RuleRouter.PickRoute(config, new RouteContext("", "", "x.com", 80, 12345));
Check("默认直连", direct.Action == "direct");

var viaProxy = RuleRouter.PickRoute(config, new RouteContext("", "", "x.com", 8080, 12345));
Check("端口规则走代理", viaProxy.Action == "proxy" && viaProxy.Upstream!.Name == "p1");

config.Proxies[0].Enabled = false;
var disabledProxy = RuleRouter.PickRoute(config, new RouteContext("", "", "x.com", 8080, 12345));
Check("已停用上游不参与路由", disabledProxy.Action == "direct" && disabledProxy.Upstream is null);
config.Proxies[0].Enabled = true;

config.Rules.Add(new ProxyRule { Name = "r2", MatchType = "dest_host", MatchValue = "blocked.com", Action = "block" });
var blocked = RuleRouter.PickRoute(config, new RouteContext("", "", "blocked.com", 80, 12345));
Check("域名规则阻止", blocked.Action == "block");

var folderConfig = new AppConfig { DefaultAction = "direct" };
folderConfig.Proxies.Add(new UpstreamConfig { Name = "p1", Type = "http", Host = "127.0.0.1", Port = 7890, Enabled = true });
folderConfig.Rules.Add(new ProxyRule { Name = "game", MatchType = "process", MatchValue = @"C:\Games\", Action = "proxy", Proxy = "p1" });
var folder = RuleRouter.PickRoute(folderConfig, new RouteContext("game", @"c:\games\game.exe", "x.com", 80, 12345));
Check("进程文件夹规则", folder.Action == "proxy");

// ---- 7. 端到端：启动真实代理引擎，直连回环目标 ----
var httpPort = FreePort();
var socksPort = FreePort();
var engineConfig = new AppConfig
{
    ListenHost = "127.0.0.1",
    HttpPort = httpPort,
    Socks5Port = socksPort,
    DefaultAction = "direct"
};
var engine = new ProxyEngine(() => engineConfig);
engine.Start();
Check("引擎启动", engine.Running);
Check("HTTP 监听就绪", engine.HttpListening);

var echoListener = new TcpListener(IPAddress.Loopback, 0);
echoListener.Start();
var echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;
var echoServer = Task.Run(async () =>
{
    for (var i = 0; i < 4; i++)
    {
        TcpClient client;
        try
        {
            client = await echoListener.AcceptTcpClientAsync();
        }
        catch
        {
            break;
        }
        _ = EchoOnceAsync(client);
    }
});
async Task EchoOnceAsync(TcpClient client)
{
    using (client)
    using (var stream = client.GetStream())
    {
        var buffer = new byte[8192];
        try
        {
            var n = await stream.ReadAsync(buffer);
            await stream.WriteAsync(buffer.AsMemory(0, n));
        }
        catch
        {
        }
    }
}

try
{
    using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
    overallTimeout.CancelAfter(TimeSpan.FromSeconds(40));
    var timeout = overallTimeout.Token;

    // 5a. HTTP 绝对式请求 → 直连目标（应被改写成 origin-form）
    using (var proxyClient = new TcpClient())
    {
        await proxyClient.ConnectAsync("127.0.0.1", httpPort, timeout);
        using var proxyStream = proxyClient.GetStream();
        var request = $"GET http://127.0.0.1:{echoPort}/hello HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n";
        await proxyStream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout);
        proxyClient.Client.Shutdown(SocketShutdown.Send); // 关闭写方向，让代理感知客户端结束并关闭隧道
        var response = await ReadToEndAsync(proxyStream, timeout);
        Check("HTTP 绝对式请求经代理转发", response.Contains("GET /hello HTTP/1.1"));
    }

    // 5b. CONNECT 隧道 → 直连目标
    using (var tunnelClient = new TcpClient())
    {
        await tunnelClient.ConnectAsync("127.0.0.1", httpPort);
        using var tunnelStream = tunnelClient.GetStream();
        var connectRequest = $"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n";
        await tunnelStream.WriteAsync(Encoding.ASCII.GetBytes(connectRequest));
        var header = await ReadUntilAsync(tunnelStream, "\r\n\r\n");
        Check("CONNECT 隧道建立 200", header.Contains("200 Connection established"));
        await tunnelStream.WriteAsync(Encoding.ASCII.GetBytes("ping"));
        var echo = new byte[4];
        var read = await ReadToBufferAsync(tunnelStream, echo);
        Check("CONNECT 隧道双向转发", read == 4 && Encoding.ASCII.GetString(echo) == "ping");
    }

    // 5c. SOCKS5 CONNECT → 直连目标
    using (var socksClient = new TcpClient())
    {
        await socksClient.ConnectAsync("127.0.0.1", socksPort);
        using var socksStream = socksClient.GetStream();
        await socksStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var handshake = new byte[2];
        await ReadToBufferAsync(socksStream, handshake);
        Check("SOCKS5 握手", handshake[0] == 0x05 && handshake[1] == 0x00);
        var portBytes = new byte[] { (byte)(echoPort >> 8), (byte)(echoPort & 0xff) };
        var connect = new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, portBytes[0], portBytes[1] };
        await socksStream.WriteAsync(connect);
        var reply = new byte[10];
        await ReadToBufferAsync(socksStream, reply);
        Check("SOCKS5 CONNECT 成功", reply[1] == 0x00);
        await socksStream.WriteAsync(Encoding.ASCII.GetBytes("socks"));
        var echo2 = new byte[5];
        var read2 = await ReadToBufferAsync(socksStream, echo2);
        Check("SOCKS5 隧道转发", read2 == 5 && Encoding.ASCII.GetString(echo2) == "socks");
    }
}
finally
{
    engine.Stop();
    echoListener.Stop();
    await echoServer;
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部测试通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition() && DateTime.UtcNow < deadline)
    {
        await Task.Delay(25);
    }
}

static async Task<string> ReadToEndAsync(NetworkStream stream, CancellationToken token)
{
    var builder = new StringBuilder();
    var buffer = new byte[4096];
    while (true)
    {
        var n = await stream.ReadAsync(buffer, token);
        if (n == 0) break;
        builder.Append(Encoding.ASCII.GetString(buffer, 0, n));
    }
    return builder.ToString();
}

static async Task<string> ReadUntilAsync(NetworkStream stream, string marker)
{
    var builder = new StringBuilder();
    var buffer = new byte[1];
    while (!builder.ToString().Contains(marker, StringComparison.Ordinal))
    {
        var n = await stream.ReadAsync(buffer);
        if (n == 0) throw new IOException("连接提前关闭");
        builder.Append(Encoding.ASCII.GetString(buffer, 0, n));
    }
    return builder.ToString();
}

static async Task<int> ReadToBufferAsync(NetworkStream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
        if (n == 0) throw new IOException("连接提前关闭");
        offset += n;
    }
    return offset;
}
