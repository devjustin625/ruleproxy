using System.IO;
using System.Text.Json;

namespace RuleProxy.Native.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ruleproxy",
        "config.json");

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (config is not null)
                {
                    return config;
                }
            }
        }
        catch (JsonException)
        {
        }

        var defaultConfig = CreateDefault();
        Save(defaultConfig);
        return defaultConfig;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temporaryPath = ConfigPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporaryPath, ConfigPath, true);
    }

    private static AppConfig CreateDefault() => new()
    {
        Proxies =
        [
            new UpstreamConfig { Name = "我的代理", Type = "http", Host = "127.0.0.1", Port = 7890 }
        ],
        Rules =
        [
            new ProxyRule
            {
                Name = "常规端口直连", MatchType = "dest_port", MatchValue = "80,443",
                Action = "direct", Note = "80/443 端口走直连"
            },
            new ProxyRule
            {
                Name = "8080 代理", MatchType = "dest_port", MatchValue = "8080",
                Action = "proxy", Proxy = "我的代理", Note = "8080 端口走代理"
            },
            new ProxyRule
            {
                Name = "指定应用代理", Enabled = false, MatchType = "process",
                MatchValue = "steam.exe,origin.exe", Action = "proxy", Proxy = "我的代理"
            }
        ]
    };
}