using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RuleProxy.Native.Core;

/// <summary>通过系统 TCP 连接表（GetExtendedTcpTable）把“本地源端口 → 进程”建立映射，
/// 后台每 1 秒全量刷新，实现“分应用”规则。等价于 Python 版的 psutil 方案。</summary>
public sealed class ProcessDetector
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    private readonly object _lock = new();
    private Dictionary<int, (int Pid, string Name)> _map = new();
    private readonly Dictionary<int, string> _nameCache = new();
    private readonly Dictionary<int, string> _exeCache = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private const double RefreshInterval = 1.0;
    private const double MinScanInterval = 0.5;

    public async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Refresh(); }
            catch { }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Refresh(bool force = false)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastRefresh).TotalSeconds < RefreshInterval)
            {
                return;
            }
            _lastRefresh = now;
            var map = new Dictionary<int, (int, string)>();
            foreach (var (localPort, pid) in EnumerateTcpConnections())
            {
                if (localPort > 0 && pid > 0)
                {
                    map[localPort] = (pid, Name(pid));
                }
            }
            if (map.Count > 0)
            {
                _map = map;
            }
        }
    }

    /// <summary>返回 (pid, 进程名, exe 路径)；找不到时返回 (null, "", "")。</summary>
    public (int? Pid, string Name, string Exe) ProcessForPort(int port, bool needExe, bool allowScan)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(port, out var info))
            {
                return (info.Pid, info.Name, needExe ? Exe(info.Pid) : "");
            }
        }

        if (allowScan)
        {
            Refresh(force: true);
            lock (_lock)
            {
                if (_map.TryGetValue(port, out var info2))
                {
                    return (info2.Pid, info2.Name, needExe ? Exe(info2.Pid) : "");
                }
            }
            Thread.Sleep(50);
            Refresh(force: true);
            lock (_lock)
            {
                if (_map.TryGetValue(port, out var info3))
                {
                    return (info3.Pid, info3.Name, needExe ? Exe(info3.Pid) : "");
                }
            }
        }
        return (null, "", "");
    }

    private string Name(int pid)
    {
        if (_nameCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }
        var name = pid.ToString();
        try
        {
            name = Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            name = pid.ToString();
        }
        _nameCache[pid] = name;
        if (_nameCache.Count > 2000)
        {
            _nameCache.Clear();
        }
        return name;
    }

    private string Exe(int pid)
    {
        if (_exeCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }
        var path = "";
        try
        {
            path = Process.GetProcessById(pid).MainModule?.FileName ?? "";
        }
        catch
        {
            path = "";
        }
        _exeCache[pid] = path;
        if (_exeCache.Count > 2000)
        {
            _exeCache.Clear();
        }
        return path;
    }

    private static IEnumerable<(int LocalPort, int Pid)> EnumerateTcpConnections()
    {
        var bufferSize = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AfInet, TcpTableOwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer && result != ErrorSuccess)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(buffer, ref bufferSize, false, AfInet, TcpTableOwnerPidAll, 0);
            if (result != ErrorSuccess)
            {
                yield break;
            }
            var numEntries = Marshal.ReadInt32(buffer);
            var pointer = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(pointer);
                pointer = IntPtr.Add(pointer, rowSize);
                if (row.LocalPort > 0 && row.OwningPid > 0)
                {
                    yield return (Ntohs(row.LocalPort), (int)row.OwningPid);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int Ntohs(uint value) => (int)((value >> 8) | (value << 8)) & 0xffff;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);
}
