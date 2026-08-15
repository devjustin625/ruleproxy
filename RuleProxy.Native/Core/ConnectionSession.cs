namespace RuleProxy.Native.Core;

/// <summary>单条代理连接的记录（供界面展示与统计）。</summary>
public sealed class ConnectionSession
{
    private long _upBytes;
    private long _downBytes;
    private int _done;

    public int Id { get; init; }
    public string Timestamp { get; init; } = "";
    public int? Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string DestHost { get; set; } = "";
    public int DestPort { get; set; }
    public int SrcPort { get; set; }
    public string RuleName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "连接中";
    public long UpBytes => Interlocked.Read(ref _upBytes);
    public long DownBytes => Interlocked.Read(ref _downBytes);
    public bool Done => Volatile.Read(ref _done) != 0;

    public void AddUpBytes(long count) => Interlocked.Add(ref _upBytes, count);
    public void AddDownBytes(long count) => Interlocked.Add(ref _downBytes, count);
    public bool TryComplete() => Interlocked.Exchange(ref _done, 1) == 0;

    public string Destination => DestPort > 0 ? $"{DestHost}:{DestPort}" : DestHost;
    public string ActionText => Action switch
    {
        "proxy" => "代理",
        "block" => "阻止",
        _ => "直连"
    };
}
