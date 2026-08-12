using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace RuleProxy.Native.Core;

public static class RuleRouter
{
    public static RouteResult PickRoute(AppConfig config, RouteContext context)
    {
        foreach (var rule in config.Rules)
        {
            if (!Matches(rule, context))
            {
                continue;
            }

            return BuildResult(config, rule.Action, rule.Proxy, rule.Name);
        }

        return BuildResult(config, config.DefaultAction, config.DefaultProxy, "默认规则");
    }

    public static bool NeedsProcess(AppConfig config) =>
        config.Rules.Any(rule => rule.Enabled && rule.MatchType == "process");

    public static bool UsesPathRules(AppConfig config) =>
        config.Rules.Any(rule => rule.Enabled && rule.MatchType == "process" &&
            SplitValues(rule.MatchValue).Any(IsPathValue));

    public static HashSet<int> ParsePorts(string value)
    {
        var ports = new HashSet<int>();
        foreach (var part in SplitValues(value))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (bounds.Length == 2 && int.TryParse(bounds[0], out var start) &&
                int.TryParse(bounds[1], out var end))
            {
                for (var port = Math.Max(0, start); port <= Math.Min(65535, end); port++)
                {
                    ports.Add(port);
                }
            }
            else if (int.TryParse(part, out var port) && port is >= 0 and <= 65535)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    public static bool HostMatches(string pattern, string host)
    {
        var normalizedPattern = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalizedPattern.Length == 0 || normalizedHost.Length == 0)
        {
            return false;
        }

        if (normalizedPattern == "*")
        {
            return true;
        }

        if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalizedPattern[2..];
            return normalizedHost == suffix || normalizedHost.EndsWith('.' + suffix, StringComparison.Ordinal);
        }

        if (!normalizedPattern.Contains('*') && !normalizedPattern.Contains('?'))
        {
            return normalizedHost == normalizedPattern;
        }

        var regex = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(normalizedHost, regex, RegexOptions.CultureInvariant);
    }

    private static bool Matches(ProxyRule rule, RouteContext context)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        return rule.MatchType switch
        {
            "process" => SplitValues(rule.MatchValue).Any(value =>
                ProcessMatches(value, context.Process, context.ProcessExe)),
            "dest_port" => ParsePorts(rule.MatchValue).Contains(context.DestinationPort),
            "src_port" => ParsePorts(rule.MatchValue).Contains(context.SourcePort),
            "dest_host" => SplitValues(rule.MatchValue).Any(value => HostMatches(value, context.DestinationHost)),
            _ => false
        };
    }

    private static bool ProcessMatches(string value, string processName, string executablePath)
    {
        if (IsPathValue(value))
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var expected = NormalizePath(value);
            var actual = NormalizePath(executablePath);
            if (value.Contains('*') || value.Contains('?'))
            {
                var regex = "^" + Regex.Escape(expected).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(actual, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (value.EndsWith('\\') || value.EndsWith('/') || Directory.Exists(value))
            {
                expected = expected.TrimEnd('\\');
                return actual == expected || actual.StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase);
            }

            return actual == expected;
        }

        return processName.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            (value.Length >= 2 && processName.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }

    private static RouteResult BuildResult(AppConfig config, string action, string proxyName, string ruleName)
    {
        if (action == "block" || action == "direct")
        {
            return new RouteResult(action, null, ruleName);
        }

        var upstream = FindUpstream(config, proxyName);
        return upstream is null
            ? new RouteResult("direct", null, ruleName + "（无可用代理→直连）")
            : new RouteResult("proxy", upstream, ruleName);
    }

    private static UpstreamConfig? FindUpstream(AppConfig config, string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var named = config.Proxies.FirstOrDefault(proxy => proxy.Name == name && proxy.Enabled)
                ?? config.Proxies.FirstOrDefault(proxy => proxy.Name == name);
            if (named is not null)
            {
                return named;
            }
        }

        return config.Proxies.FirstOrDefault(proxy => proxy.Enabled);
    }

    private static IEnumerable<string> SplitValues(string value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsPathValue(string value) =>
        value.Contains('\\') || value.Contains('/') || Path.IsPathRooted(value);

    private static string NormalizePath(string value) => value.Replace('/', '\\').ToLowerInvariant();
}