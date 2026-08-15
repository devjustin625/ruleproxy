using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RuleProxy.Native.Core;

/// <summary>异步代理引擎：HTTP 代理 + SOCKS5 代理服务端。
/// 流程：源端口反查进程 → 解析目标 → 匹配规则 → 直连/上游/阻止 → 双向转发。</summary>
public sealed class ProxyEngine
{
    private const int MaxHistory = 200;
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly Func<AppConfig> _configGetter;
    private readonly ProcessDetector _detector = new();
    private readonly ConcurrentDictionary<int, ConnectionSession> _active = new();
    private readonly List<ConnectionSession> _history = new();
    private readonly object _historyLock = new();
    private readonly List<string> _logs = new();
    private readonly object _logsLock = new();
    private readonly List<string> _pendingLogs = new();

    private TcpListener? _httpListener;
    private TcpListener? _socksListener;
    private CancellationTokenSource? _cts;
    private int _nextId;
    private long _totalUp;
    private long _totalDown;

    public event Action? StateChanged;
    public event Action? LogsChanged;

    public bool Running => _cts is not null && (_httpListener is not null || _socksListener is not null);
    public bool HttpListening => _httpListener is not null;

    // ------------------------------------------------------------- 生命周期

    public ProxyEngine(Func<AppConfig> configGetter)
    {
        _configGetter = configGetter;
    }

    public void Start()
    {
        if (Running)
        {
            return;
        }
        var cfg = _configGetter();
        if (!cfg.IsValidForListening(out var configError))
        {
            Log($"代理引擎启动失败：{configError}");
            StateChanged?.Invoke();
            return;
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => _ = _detector.RefreshLoopAsync(token), token);

        try
        {
            _httpListener = new TcpListener(IPAddress.Parse(cfg.ListenHost), cfg.HttpPort);
            _httpListener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_httpListener, isSocks: false, token), token);
            Log($"HTTP 代理监听 {cfg.ListenHost}:{cfg.HttpPort}");
        }
        catch (Exception e)
        {
            Log($"HTTP 代理启动失败（{cfg.ListenHost}:{cfg.HttpPort}）: {e.Message}");
            _httpListener = null;
        }

        try
        {
            _socksListener = new TcpListener(IPAddress.Parse(cfg.ListenHost), cfg.Socks5Port);
            _socksListener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_socksListener, isSocks: true, token), token);
            Log($"SOCKS5 代理监听 {cfg.ListenHost}:{cfg.Socks5Port}");
        }
        catch (Exception e)
        {
            Log($"SOCKS5 代理启动失败（{cfg.ListenHost}:{cfg.Socks5Port}）: {e.Message}");
            _socksListener = null;
        }

        if (_httpListener is null && _socksListener is null)
        {
            Log("代理引擎启动失败：无可用监听端口");
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            return;
        }

        Log("代理引擎已就绪");
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }
        _cts?.Cancel();
        try { _httpListener?.Stop(); } catch { }
        try { _socksListener?.Stop(); } catch { }
        _httpListener = null;
        _socksListener = null;
        _cts?.Dispose();
        _cts = null;

        foreach (var id in _active.Keys.ToList())
        {
            if (_active.TryGetValue(id, out var session))
            {
                Finalize(session, "已停止");
            }
        }
        Log("代理引擎已停止");
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------- 统计 / 日志

    public (List<ConnectionSession> Active, List<ConnectionSession> History, long TotalUp, long TotalDown) Snapshot()
    {
        var active = _active.Values.Select(Clone).ToList();
        List<ConnectionSession> history;
        long totalUp;
        long totalDown;
        lock (_historyLock)
        {
            history = _history.Select(Clone).ToList();
            totalUp = _totalUp;
            totalDown = _totalDown;
        }
        return (active, history, totalUp, totalDown);
    }

    public void ClearHistory()
    {
        lock (_historyLock)
        {
            _history.Clear();
        }
        StateChanged?.Invoke();
    }

    public IReadOnlyList<string> DrainLogs()
    {
        lock (_logsLock)
        {
            if (_pendingLogs.Count == 0)
            {
                return Array.Empty<string>();
            }
            var items = _pendingLogs.ToArray();
            _pendingLogs.Clear();
            return items;
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logsLock)
        {
            _logs.Add(line);
            _pendingLogs.Add(line);
            if (_logs.Count > 1000)
            {
                _logs.RemoveAt(0);
            }
        }
        LogsChanged?.Invoke();
    }

    // ------------------------------------------------------------- 连接处理

    private async Task AcceptLoopAsync(TcpListener listener, bool isSocks, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    MarkListenerUnavailable(listener, isSocks, e);
                }
                break;
            }
            _ = Task.Run(() => HandleConnectionAsync(client, isSocks, ct), CancellationToken.None);
        }
    }

    private void MarkListenerUnavailable(TcpListener listener, bool isSocks, Exception error)
    {
        var cleared = isSocks
            ? Interlocked.CompareExchange(ref _socksListener, null, listener) == listener
            : Interlocked.CompareExchange(ref _httpListener, null, listener) == listener;
        if (!cleared)
        {
            return;
        }

        Log($"{(isSocks ? "SOCKS5" : "HTTP")} 代理监听异常退出: {error.Message}");
        StateChanged?.Invoke();
        if (_httpListener is null && _socksListener is null)
        {
            Stop();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, bool isSocks, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var srcPort = remote?.Port ?? 0;
        var session = NewSession(srcPort);
        try
        {
            client.Client.NoDelay = true;
            var cfg = _configGetter();
            var (pid, name, exe) = _detector.ProcessForPort(
                srcPort,
                needExe: RuleRouter.UsesPathRules(cfg),
                allowScan: RuleRouter.NeedsProcess(cfg));
            session.Pid = pid;
            session.ProcessName = name;
            session.ExePath = exe;

            await using var clientStream = client.GetStream();
            if (isSocks)
            {
                await HandleSocksAsync(clientStream, session, cfg, ct);
            }
            else
            {
                await HandleHttpAsync(clientStream, session, cfg, ct);
            }
        }
        catch (Exception e)
        {
            Log($"连接异常（端口 {srcPort}）: {e.Message}");
        }
        finally
        {
            Finalize(session, "已断开");
            client.Close();
        }
    }

    private async Task HandleHttpAsync(Stream clientStream, ConnectionSession session, AppConfig cfg, CancellationToken ct)
    {
        var (head, extra) = await ReadHttpHeadAsync(clientStream, ct);
        if (head.Length == 0)
        {
            return;
        }
        var headText = Encoding.Latin1.GetString(head);
        var lines = headText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3)
        {
            await WriteHttpErrorAsync(clientStream, 400, "Bad Request", ct);
            return;
        }
        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];
        var version = requestLine[2];

        if (method == "CONNECT")
        {
            var (host, port) = SplitHostPort(target, 443);
            session.DestHost = host;
            session.DestPort = port;
            var route = RouteFor(session, cfg);
            session.RuleName = route.RuleName;
            session.Action = route.Action;
            if (route.Action == "block")
            {
                await WriteHttpErrorAsync(clientStream, 403, "Blocked by rules", ct);
                return;
            }
            NetworkStream? remote = null;
            try
            {
                remote = await UpstreamClient.ConnectViaAsync(route, host, port, ct);
            }
            catch (Exception e)
            {
                Log($"CONNECT {host}:{port} 失败（{route.RuleName}）: {e.Message}");
                await WriteHttpErrorAsync(clientStream, 502, "Upstream error", ct);
                return;
            }
            await using (remote)
            {
                await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
                session.Status = "已连接";
                if (extra.Length > 0)
                {
                    await remote.WriteAsync(extra, ct);
                    session.AddUpBytes(extra.Length);
                }
                await RelayAsync(clientStream, remote, session, ct);
            }
            return;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            await WriteHttpErrorAsync(clientStream, 400, "请使用代理模式（绝对形式 URL）", ct);
            return;
        }
        var hostName = uri.Host;
        var hostPort = uri.Port;
        session.DestHost = hostName;
        session.DestPort = hostPort;
        var httpRoute = RouteFor(session, cfg);
        session.RuleName = httpRoute.RuleName;
        session.Action = httpRoute.Action;
        if (httpRoute.Action == "block")
        {
            await WriteHttpErrorAsync(clientStream, 403, "Blocked by rules", ct);
            return;
        }
        NetworkStream? remoteStream = null;
        try
        {
            remoteStream = await UpstreamClient.ConnectViaAsync(httpRoute, hostName, hostPort, ct);
        }
        catch (Exception e)
        {
            Log($"HTTP {target} 失败（{httpRoute.RuleName}）: {e.Message}");
            await WriteHttpErrorAsync(clientStream, 502, "Upstream error", ct);
            return;
        }
        await using (remoteStream)
        {
            // 直连 / SOCKS5 上游时改写为目标路径形式；HTTP 上游保留绝对形式
            var rewrite = httpRoute.Action == "direct" ||
                (httpRoute.Upstream is not null && httpRoute.Upstream.Type == "socks5");
            byte[] headBytes;
            if (rewrite)
            {
                var firstCrlf = IndexOf(head, (byte)'\r');
                var rewritten = Encoding.ASCII.GetBytes($"{method} {uri.PathAndQuery} {version}");
                var buffer = new byte[rewritten.Length + head.Length - firstCrlf];
                rewritten.CopyTo(buffer, 0);
                Array.Copy(head, firstCrlf, buffer, rewritten.Length, head.Length - firstCrlf);
                headBytes = buffer;
            }
            else
            {
                headBytes = head;
            }
            headBytes = AddConnectionCloseHeader(headBytes);
            await remoteStream.WriteAsync(headBytes, ct);
            session.AddUpBytes(headBytes.Length + extra.Length);
            if (extra.Length > 0)
            {
                await remoteStream.WriteAsync(extra, ct);
            }
            session.Status = "已连接";
            await RelayAsync(clientStream, remoteStream, session, ct);
        }
    }

    private async Task HandleSocksAsync(Stream clientStream, ConnectionSession session, AppConfig cfg, CancellationToken ct)
    {
        NetworkStream? remote = null;
        string host = "";
        var port = 0;
        RouteResult route = new("direct", null, "默认规则");

        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var token = handshake.Token;

            var header = await RecvExactAsync(clientStream, 2, token);
            if (header.Length < 2 || header[0] != 0x05)
            {
                return;
            }
            var methodsCount = header[1];
            var methods = methodsCount > 0
                ? await RecvExactAsync(clientStream, methodsCount, token)
                : Array.Empty<byte>();
            if (!methods.Contains((byte)0x00))
            {
                await clientStream.WriteAsync(new byte[] { 0x05, 0xFF }, token);
                return;
            }
            await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, token); // 无认证

            var request = await RecvExactAsync(clientStream, 4, token);
            if (request.Length < 4)
            {
                return;
            }
            var command = request[1];
            var atyp = request[3];
            if (command != 0x01)
            {
                await clientStream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token);
                return;
            }
            if (atyp == 0x01)
            {
                host = new IPAddress(await RecvExactAsync(clientStream, 4, token)).ToString();
            }
            else if (atyp == 0x03)
            {
                var length = (await RecvExactAsync(clientStream, 1, token))[0];
                host = Encoding.UTF8.GetString(await RecvExactAsync(clientStream, length, token));
            }
            else if (atyp == 0x04)
            {
                host = new IPAddress(await RecvExactAsync(clientStream, 16, token)).ToString();
            }
            else
            {
                await clientStream.WriteAsync(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token);
                return;
            }
            var portBytes = await RecvExactAsync(clientStream, 2, token);
            port = (portBytes[0] << 8) | portBytes[1];

            session.DestHost = host;
            session.DestPort = port;
            route = RouteFor(session, cfg);
            session.RuleName = route.RuleName;
            session.Action = route.Action;
            if (route.Action == "block")
            {
                await clientStream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token);
                return;
            }
            try
            {
                remote = await UpstreamClient.ConnectViaAsync(route, host, port, ct);
            }
            catch (Exception e)
            {
                Log($"SOCKS5 {host}:{port} 连接失败（{route.RuleName}）: {e.Message}");
                await clientStream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token);
                return;
            }
        }

        await using (remote)
        {
            await clientStream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            session.Status = "已连接";
            await RelayAsync(clientStream, remote, session, ct);
        }
    }

    // ------------------------------------------------------------- 会话管理

    private ConnectionSession NewSession(int srcPort)
    {
        var id = Interlocked.Increment(ref _nextId);
        var session = new ConnectionSession
        {
            Id = id,
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            SrcPort = srcPort
        };
        _active[id] = session;
        return session;
    }

    private RouteResult RouteFor(ConnectionSession session, AppConfig cfg) =>
        RuleRouter.PickRoute(cfg, new RouteContext(
            session.ProcessName,
            session.ExePath,
            session.DestHost,
            session.DestPort,
            session.SrcPort));

    private void Finalize(ConnectionSession session, string status)
    {
        if (!session.TryComplete())
        {
            return;
        }
        session.Status = status;
        _active.TryRemove(session.Id, out _);
        lock (_historyLock)
        {
            _history.Insert(0, session);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            }
            _totalUp += session.UpBytes;
            _totalDown += session.DownBytes;
        }
    }

    private static ConnectionSession Clone(ConnectionSession s)
    {
        var clone = new ConnectionSession
        {
            Id = s.Id,
            Timestamp = s.Timestamp,
            Pid = s.Pid,
            ProcessName = s.ProcessName,
            ExePath = s.ExePath,
            DestHost = s.DestHost,
            DestPort = s.DestPort,
            SrcPort = s.SrcPort,
            RuleName = s.RuleName,
            Action = s.Action,
            Status = s.Status
        };
        clone.AddUpBytes(s.UpBytes);
        clone.AddDownBytes(s.DownBytes);
        if (s.Done)
        {
            clone.TryComplete();
        }
        return clone;
    }

    // ------------------------------------------------------------- 工具

    private static byte[] AddConnectionCloseHeader(byte[] head)
    {
        var text = Encoding.Latin1.GetString(head);
        var marker = "\r\n\r\n";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return head;
        }

        var withoutEnd = text[..index];
        var lines = withoutEnd.Split("\r\n", StringSplitOptions.None)
            .Where(line => !line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase));
        return Encoding.Latin1.GetBytes(string.Join("\r\n", lines) + "\r\nConnection: close\r\n\r\n");
    }

    private static async Task RelayAsync(Stream client, Stream remote, ConnectionSession session, CancellationToken ct)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = PumpAsync(client, remote, session.AddUpBytes, relayCts.Token);
        var down = PumpAsync(remote, client, session.AddDownBytes, relayCts.Token);
        await Task.WhenAll(up, down);
    }

    private static async Task PumpAsync(Stream from, Stream to, Action<long> counter, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0)
                {
                    break;
                }
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                counter(read);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            TryShutdownSend(to);
            try
            {
                to.Flush();
            }
            catch
            {
            }
        }
    }

    private static void TryShutdownSend(Stream stream)
    {
        if (stream is NetworkStream networkStream)
        {
            try { networkStream.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private static async Task<(byte[] Head, byte[] Extra)> ReadHttpHeadAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var buffer = new byte[4096];
        var collected = new List<byte>(4096);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token);
            if (read == 0)
            {
                return (collected.ToArray(), Array.Empty<byte>());
            }
            for (var i = 0; i < read; i++)
            {
                collected.Add(buffer[i]);
            }
            if (collected.Count > MaxHeaderBytes)
            {
                throw new IOException("请求头过大");
            }
            var marker = FindHeaderMarker(collected);
            if (marker >= 0)
            {
                var headLength = marker + 4;
                var head = collected.Take(headLength).ToArray();
                var extra = collected.Skip(headLength).ToArray();
                return (head, extra);
            }
        }
    }

    private static int FindHeaderMarker(List<byte> data)
    {
        for (var i = 0; i <= data.Count - 4; i++)
        {
            if (data[i] == 0x0d && data[i + 1] == 0x0a && data[i + 2] == 0x0d && data[i + 3] == 0x0a)
            {
                return i;
            }
        }
        return -1;
    }

    private static int IndexOf(byte[] data, byte value)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == value)
            {
                return i;
            }
        }
        return data.Length;
    }

    private static async Task<byte[]> RecvExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
            {
                throw new IOException("连接提前关闭");
            }
            offset += read;
        }
        return buffer;
    }

    private static async Task WriteHttpErrorAsync(Stream stream, int code, string reason, CancellationToken ct)
    {
        var body = $"<html><body><h1>{code} {reason}</h1></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var response =
            $"HTTP/1.1 {code} {reason}\r\n" +
            $"Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n" +
            body;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
    }

    private static (string Host, int Port) SplitHostPort(string target, int defaultPort)
    {
        var value = target;
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end > 0)
            {
                var host = value[1..end];
                var rest = value[(end + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var v6Port))
                {
                    return (host, v6Port);
                }
                return (host, defaultPort);
            }
        }
        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out var parsedPort))
        {
            return (value[..colon], parsedPort);
        }
        return (value, defaultPort);
    }
}
