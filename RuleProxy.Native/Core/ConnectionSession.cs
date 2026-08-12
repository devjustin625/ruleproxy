namespace RuleProxy.Native.Core;

/// <summary>单条代理连接的记录（供界面展示与统计）。</summary>
public sealed class ConnectionSession
{
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
    public long UpBytes { get; set; }
    public long DownBytes { get; set; }
    public bool Done { get; set; }

    public string Destination => DestPort > 0 ? $"{DestHost}:{DestPort}" : DestHost;
    public string ActionText => Action switch
    {
        "proxy" => "代理",
        "block" => "阻止",
        _ => "直连"
    };
}
