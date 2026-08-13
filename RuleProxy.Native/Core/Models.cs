using System.Text.Json.Serialization;

namespace RuleProxy.Native.Core;

public sealed class UpstreamConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "默认代理";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "http";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 7890;

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string TypeText => Type == "socks5" ? "SOCKS5" : "HTTP";
}

public sealed class ProxyRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "新规则";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("match_type")]
    public string MatchType { get; set; } = "dest_port";

    [JsonPropertyName("match_value")]
    public string MatchValue { get; set; } = "8080";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "proxy";

    [JsonPropertyName("proxy")]
    public string Proxy { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonIgnore]
    public string MatchTypeText => MatchType switch
    {
        "process" => "应用进程",
        "dest_host" => "目标主机/域名",
        "src_port" => "客户端源端口",
        _ => "目标端口"
    };

    [JsonIgnore]
    public string ActionText => Action switch
    {
        "proxy" => "代理",
        "block" => "阻止",
        _ => "直连"
    };
}

public sealed class AppConfig
{
    [JsonPropertyName("listen_host")]
    public string ListenHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 8888;

    [JsonPropertyName("socks5_port")]
    public int Socks5Port { get; set; } = 8889;

    [JsonPropertyName("default_action")]
    public string DefaultAction { get; set; } = "direct";

    [JsonPropertyName("default_proxy")]
    public string DefaultProxy { get; set; } = "";

    [JsonPropertyName("start_minimized")]
    public bool StartMinimized { get; set; }

    [JsonPropertyName("auto_start_proxy")]
    public bool AutoStartProxy { get; set; }

    [JsonPropertyName("remember_last_state")]
    public bool RememberLastState { get; set; }

    [JsonPropertyName("last_proxy_running")]
    public bool LastProxyRunning { get; set; }

    [JsonPropertyName("last_sysproxy_enabled")]
    public bool LastSysProxyEnabled { get; set; }

    [JsonPropertyName("rules")]
    public List<ProxyRule> Rules { get; set; } = [];

    [JsonPropertyName("proxies")]
    public List<UpstreamConfig> Proxies { get; set; } = [];
}

public sealed record RouteResult(string Action, UpstreamConfig? Upstream, string RuleName);

public sealed record RouteContext(
    string Process,
    string ProcessExe,
    string DestinationHost,
    int DestinationPort,
    int SourcePort);