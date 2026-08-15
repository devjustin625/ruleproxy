using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RuleProxy.Native.Core;

/// <summary>建立到目标主机/端口的连接：直连、经 HTTP 代理 CONNECT、经 SOCKS5 代理。</summary>
public static class UpstreamClient
{
    private static readonly Dictionary<string, (DateTime Time, IPAddress[] Addresses)> DnsCache = new();
    private static readonly object DnsLock = new();
    private const double DnsTtlSeconds = 60.0;

    /// <summary>根据路由结果建立连接。proxy 上游失败时小退避重试最多 3 次（防上游切换断连）。</summary>
    public static async Task<NetworkStream> ConnectViaAsync(
        RouteResult route, string host, int port, CancellationToken ct = default)
    {
        if (route.Action == "proxy" && route.Upstream is not null)
        {
            var upstream = route.Upstream;
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var attemptToken = attemptTimeout.Token;
                try
                {
                    var socket = upstream.Type == "socks5"
                        ? await ConnectSocks5Async(upstream, host, port, attemptToken)
                        : await ConnectHttpProxyAsync(upstream, host, port, attemptToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception e) when (e is SocketException or IOException or InvalidOperationException or OperationCanceledException)
                {
                    last = e;
                    if (attempt < 2)
                    {
                        try { await Task.Delay(150 * (attempt + 1), ct); }
                        catch (OperationCanceledException) { throw; }
                    }
                }
            }
            throw new InvalidOperationException($"无法连接上游代理: {host}:{port}", last);
        }

        var direct = await ConnectDirectAsync(host, port, ct);
        return new NetworkStream(direct, ownsSocket: true);
    }

    /// <summary>直连目标（域名走 60s DNS 缓存，逐个尝试所有解析地址）。</summary>
    public static async Task<Socket> ConnectDirectAsync(string host, int port, CancellationToken ct = default)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var ip))
        {
            addresses = [ip];
        }
        else
        {
            addresses = await ResolveCachedAsync(host, ct);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(address, port, ct);
                return socket;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                last = e;
                try { socket?.Dispose(); } catch { }
            }
        }
        throw new InvalidOperationException($"无法连接: {host}:{port}", last);
    }

    private static async Task<IPAddress[]> ResolveCachedAsync(string host, CancellationToken ct)
    {
        lock (DnsLock)
        {
            if (DnsCache.TryGetValue(host, out var entry) &&
                DateTime.UtcNow - entry.Time < TimeSpan.FromSeconds(DnsTtlSeconds))
            {
                return entry.Addresses;
            }
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException($"无法解析域名: {host}", e);
        }

        lock (DnsLock)
        {
            DnsCache[host] = (DateTime.UtcNow, addresses);
            if (DnsCache.Count > 512)
            {
                DnsCache.Clear();
            }
        }
        return addresses;
    }

    private static async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var ip))
        {
            addresses = [ip];
        }
        else
        {
            addresses = await ResolveCachedAsync(host, ct);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(address, port, ct);
                return socket;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                last = e;
                try { socket?.Dispose(); } catch { }
            }
        }
        throw new InvalidOperationException($"无法连接: {host}:{port}", last);
    }

    private static async Task<Socket> ConnectHttpProxyAsync(UpstreamConfig proxy, string host, int port, CancellationToken ct)
    {
        var socket = await ConnectSocketAsync(proxy.Host, proxy.Port, ct);
        var success = false;
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var target = $"{host}:{port}";
            var request = new StringBuilder($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password}"));
                request.Append($"Proxy-Authorization: Basic {token}\r\n");
            }
            request.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);
            var response = await ReadUntilAsync(stream, "\r\n\r\n", ct);
            var statusLine = response.Split("\r\n", 2, StringSplitOptions.None)[0].Split(' ');
            if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out var code) || code != 200)
            {
                throw new InvalidOperationException($"上游代理 CONNECT 失败: {statusLine[0]}");
            }
            success = true;
            return socket;
        }
        finally
        {
            if (!success)
            {
                socket.Dispose();
            }
        }
    }

    private static async Task<Socket> ConnectSocks5Async(UpstreamConfig proxy, string host, int port, CancellationToken ct)
    {
        var socket = await ConnectSocketAsync(proxy.Host, proxy.Port, ct);
        var success = false;
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, ct);
                var ver = await RecvExactAsync(stream, 2, ct);
                if (ver[0] != 0x05 || ver[1] != 0x02)
                {
                    throw new InvalidOperationException("SOCKS5 上游不支持用户名密码认证");
                }
                var user = Encoding.UTF8.GetBytes(proxy.Username);
                var pass = Encoding.UTF8.GetBytes(proxy.Password);
                var body = new List<byte> { 0x01, (byte)user.Length };
                body.AddRange(user);
                body.Add((byte)pass.Length);
                body.AddRange(pass);
                await stream.WriteAsync(body.ToArray(), ct);
                var status = await RecvExactAsync(stream, 2, ct);
                if (status[1] != 0x00)
                {
                    throw new InvalidOperationException("SOCKS5 认证失败");
                }
            }
            else
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
                var ver = await RecvExactAsync(stream, 2, ct);
                if (ver[0] != 0x05 || ver[1] != 0x00)
                {
                    throw new InvalidOperationException("SOCKS5 上游要求认证或握手失败");
                }
            }

            await stream.WriteAsync(BuildSocksRequest(host, port), ct);
            var head = await RecvExactAsync(stream, 4, ct);
            if (head[0] != 0x05 || head[1] != 0x00)
            {
                throw new InvalidOperationException($"SOCKS5 上游连接失败: REP={head[1]}");
            }
            var atyp = head[3];
            if (atyp == 0x01) await RecvExactAsync(stream, 4, ct);
            else if (atyp == 0x04) await RecvExactAsync(stream, 16, ct);
            else if (atyp == 0x03)
            {
                var len = await RecvExactAsync(stream, 1, ct);
                await RecvExactAsync(stream, len[0], ct);
            }
            await RecvExactAsync(stream, 2, ct);
            success = true;
            return socket;
        }
        finally
        {
            if (!success)
            {
                socket.Dispose();
            }
        }
    }

    private static byte[] BuildSocksRequest(string host, int port)
    {
        var portBytes = new byte[] { (byte)(port >> 8), (byte)(port & 0xff) };
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var ip6 = new byte[22];
                ip6[0] = 0x05; ip6[1] = 0x01; ip6[2] = 0x00; ip6[3] = 0x04;
                ip.GetAddressBytes().CopyTo(ip6, 4);
                ip6[20] = portBytes[0]; ip6[21] = portBytes[1];
                return ip6;
            }
            var ip4 = new byte[10];
            ip4[0] = 0x05; ip4[1] = 0x01; ip4[2] = 0x00; ip4[3] = 0x01;
            ip.GetAddressBytes().CopyTo(ip4, 4);
            ip4[8] = portBytes[0]; ip4[9] = portBytes[1];
            return ip4;
        }

        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > 255)
        {
            throw new InvalidOperationException("主机名过长");
        }
        var domain = new byte[7 + hostBytes.Length];
        domain[0] = 0x05; domain[1] = 0x01; domain[2] = 0x00; domain[3] = 0x03;
        domain[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(domain, 5);
        domain[5 + hostBytes.Length] = portBytes[0];
        domain[6 + hostBytes.Length] = portBytes[1];
        return domain;
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var collected = new List<byte>();
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (n == 0)
            {
                throw new IOException("上游代理提前关闭连接");
            }
            for (var i = 0; i < n; i++)
            {
                collected.Add(buffer[i]);
            }
            if (collected.Count > 64 * 1024)
            {
                throw new IOException("上游代理响应头过大");
            }
            if (Encoding.ASCII.GetString(collected.ToArray()).Contains(marker, StringComparison.Ordinal))
            {
                return Encoding.ASCII.GetString(collected.ToArray());
            }
        }
    }

    private static async Task<byte[]> RecvExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0)
            {
                throw new IOException("上游代理提前关闭连接");
            }
            offset += n;
        }
        return buffer;
    }
}
