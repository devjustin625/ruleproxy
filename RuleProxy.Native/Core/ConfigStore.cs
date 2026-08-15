using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;

namespace RuleProxy.Native.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ConfigPath { get; }

    public ConfigStore(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ruleproxy",
            "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (config is not null)
                {
                    Normalize(config);
                    DecryptPasswords(config);
                    return config;
                }
            }
        }
        catch (JsonException)
        {
            BackupCorruptConfig();
        }

        var defaultConfig = CreateDefault();
        Save(defaultConfig);
        return defaultConfig;
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temporaryPath = ConfigPath + ".tmp";
        var json = JsonNode.Parse(JsonSerializer.Serialize(config, JsonOptions))!.AsObject();
        if (json["proxies"] is JsonArray proxies)
        {
            foreach (var proxy in proxies.OfType<JsonObject>())
            {
                var password = proxy["password"]?.GetValue<string>() ?? "";
                proxy["password"] = ProtectPassword(password);
            }
        }
        File.WriteAllText(temporaryPath, json.ToJsonString(JsonOptions));
        File.Move(temporaryPath, ConfigPath, true);
    }

    private static void DecryptPasswords(AppConfig config)
    {
        foreach (var proxy in config.Proxies)
        {
            if (proxy.Password.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                try
                {
                    var protectedBytes = Convert.FromBase64String(proxy.Password[6..]);
                    proxy.Password = System.Text.Encoding.UTF8.GetString(
                        ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException)
                {
                    proxy.Password = "";
                }
                catch (FormatException)
                {
                    proxy.Password = "";
                }
            }
        }
    }

    private static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            return password;
        }

        var protectedBytes = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(protectedBytes);
    }

    private void BackupCorruptConfig()
    {
        try
        {
            var backupPath = ConfigPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(ConfigPath, backupPath, overwrite: false);
        }
        catch (IOException)
        {
        }
    }

    private static void Normalize(AppConfig config)
    {
        config.ListenHost = IPAddress.TryParse(config.ListenHost, out var address) && IPAddress.IsLoopback(address)
            ? address.ToString()
            : "127.0.0.1";
        config.HttpPort = NormalizePort(config.HttpPort, 8888);
        config.Socks5Port = NormalizePort(config.Socks5Port, 8889);
        if (config.HttpPort == config.Socks5Port)
        {
            config.Socks5Port = config.HttpPort == 8888 ? 8889 : 8888;
        }
        config.Rules ??= [];
        config.Proxies ??= [];
    }

    private static int NormalizePort(int port, int fallback) => port is >= 1 and <= 65535 ? port : fallback;

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