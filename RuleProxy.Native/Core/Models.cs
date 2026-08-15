using System.ComponentModel;
using System.Net;
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

    /// <summary>下拉框中的显示文本，便于区分同名代理。</summary>
    [JsonIgnore]
    public string ComboText => $"{Name}（{TypeText} {Host}:{Port}）";

    public override string ToString() => ComboText;
}

public static class UpstreamNameValidator
{
    public static bool IsAvailable(IEnumerable<UpstreamConfig> upstreams, string name, UpstreamConfig? current = null)
    {
        return !string.IsNullOrWhiteSpace(name) && !upstreams.Any(upstream =>
            !ReferenceEquals(upstream, current) &&
            string.Equals(upstream.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ProxyRule : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [JsonPropertyName("name")]
    public string Name { get; set; } = "新规则";

    private bool _enabled = true;

    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                OnPropertyChanged(nameof(Enabled));
            }
        }
    }

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

    [JsonPropertyName("check_updates")]
    public bool CheckUpdates { get; set; } = true;

    [JsonPropertyName("last_proxy_running")]
    public bool LastProxyRunning { get; set; }

    [JsonPropertyName("last_sysproxy_enabled")]
    public bool LastSysProxyEnabled { get; set; }

    [JsonPropertyName("rules")]
    public List<ProxyRule> Rules { get; set; } = [];

    [JsonPropertyName("proxies")]
    public List<UpstreamConfig> Proxies { get; set; } = [];

    public bool IsValidForListening(out string error)
    {
        if (!IPAddress.TryParse(ListenHost, out var address) || !IPAddress.IsLoopback(address))
        {
            error = "监听地址必须是回环地址（127.0.0.1 或 ::1），当前版本暂不支持远程入站连接。";
            return false;
        }

        if (HttpPort is < 1 or > 65535 || Socks5Port is < 1 or > 65535 || HttpPort == Socks5Port)
        {
            error = "HTTP 和 SOCKS5 端口必须在 1-65535 范围内且不能相同。";
            return false;
        }

        error = "";
        return true;
    }
}

public sealed record RouteResult(string Action, UpstreamConfig? Upstream, string RuleName);

public sealed record RouteContext(
    string Process,
    string ProcessExe,
    string DestinationHost,
    int DestinationPort,
    int SourcePort);