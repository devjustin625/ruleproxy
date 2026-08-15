using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuleProxy.Native.Core;

public sealed record UpdateRelease(Version Version, string TagName, string ReleaseUrl, string DownloadUrl, string Sha256);

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/devjustin625/ruleproxy/releases/latest";
    private const string AssetName = "RuleProxy.exe";
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;

    public static string UpdateLogPath => Path.Combine(Path.GetTempPath(), "RuleProxy", "updates", "update.log");

    public UpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public static Version CurrentVersion => NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version?.ToString()) ?? new Version(0, 0, 0);

    public async Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return TryParseRelease(json, CurrentVersion, out var release) ? release : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string?> DownloadAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "RuleProxy", "updates", release.Version.ToString(3));
            Directory.CreateDirectory(directory);
            var finalPath = Path.Combine(directory, AssetName);
            var temporaryPath = finalPath + ".download";
            using var response = await _httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temporaryPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (!IsExecutable(temporaryPath) || !MatchesSha256(temporaryPath, release.Sha256))
            {
                File.Delete(temporaryPath);
                return null;
            }

            File.Move(temporaryPath, finalPath, true);
            return finalPath;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public static bool TryParseRelease(string json, Version currentVersion, out UpdateRelease? release)
    {
        release = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                !root.TryGetProperty("html_url", out var pageElement) ||
                !TryGetVersion(tagElement.GetString(), out var version) || version <= currentVersion ||
                !root.TryGetProperty("assets", out var assets))
            {
                return false;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName &&
                    asset.TryGetProperty("browser_download_url", out var url) &&
                    TryGetTrustedAssetUrl(url.GetString(), out var downloadUrl) &&
                    asset.TryGetProperty("digest", out var digest) &&
                    TryGetSha256(digest.GetString(), out var sha256))
                {
                    release = new UpdateRelease(version, tagElement.GetString()!, pageElement.GetString() ?? "", downloadUrl, sha256);
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    public static bool TryGetVersion(string? value, out Version version)
    {
        var normalized = NormalizeVersion(value);
        version = normalized ?? new Version(0, 0, 0);
        return normalized is not null;
    }

    public static Version? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim().TrimStart('v', 'V');
        var suffix = clean.IndexOf('+');
        if (suffix >= 0) clean = clean[..suffix];
        return Version.TryParse(clean, out var version) ? version : null;
    }

    public static IReadOnlyList<string> BuildApplyUpdateArguments(int parentPid, string downloadedExe, string targetExe, bool minimized)
    {
        var arguments = new List<string>
        {
            "--apply-update",
            parentPid.ToString(),
            Path.GetFullPath(downloadedExe),
            Path.GetFullPath(targetExe)
        };
        if (minimized)
        {
            arguments.Add("--minimized");
        }
        return arguments;
    }

    public static bool ApplyUpdate(int parentPid, string downloadedExe, string targetExe, bool minimized)
    {
        try
        {
            downloadedExe = Path.GetFullPath(downloadedExe);
            targetExe = Path.GetFullPath(targetExe);
            WriteUpdateLog($"开始更新。下载文件: {downloadedExe}; 目标文件: {targetExe}");
            if (!File.Exists(targetExe) || !IsExecutable(downloadedExe))
            {
                WriteUpdateLog("更新取消：目标文件不存在或下载文件不是有效的可执行文件。");
                return false;
            }

            if (!WaitForParentExit(parentPid))
            {
                return false;
            }

            var backupDirectory = Path.Combine(Path.GetTempPath(), "RuleProxy", "updates", "backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, Path.GetFileName(targetExe));
            WriteUpdateLog($"备份当前程序到: {backupPath}");
            File.Copy(targetExe, backupPath, true);
            try
            {
                WriteUpdateLog("替换当前程序。");
                File.Copy(downloadedExe, targetExe, true);
            }
            catch (Exception ex)
            {
                WriteUpdateLog($"替换失败，开始回滚: {ex}");
                File.Copy(backupPath, targetExe, true);
                WriteUpdateLog("回滚完成。");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo(targetExe) { UseShellExecute = false };
                if (minimized) startInfo.ArgumentList.Add("--minimized");
                Process.Start(startInfo);
                WriteUpdateLog("更新成功，已启动新版本。");
                return true;
            }
            catch (Exception ex)
            {
                WriteUpdateLog($"启动新版本失败，开始回滚: {ex}");
                File.Copy(backupPath, targetExe, true);
                WriteUpdateLog("回滚完成。");
                return false;
            }
        }
        catch (Exception ex)
        {
            WriteUpdateLog($"更新失败: {ex}");
            return false;
        }
    }

    private static bool WaitForParentExit(int parentPid)
    {
        if (parentPid <= 0)
        {
            return true;
        }

        try
        {
            using var parent = Process.GetProcessById(parentPid);
            if (parent.WaitForExit((int)ParentExitTimeout.TotalMilliseconds))
            {
                WriteUpdateLog("主程序已退出。");
                return true;
            }

            WriteUpdateLog($"等待主程序退出超时（{ParentExitTimeout.TotalSeconds:0} 秒）。");
            return false;
        }
        catch (ArgumentException)
        {
            WriteUpdateLog("主程序已退出。");
            return true;
        }
    }

    private static void WriteUpdateLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateLogPath)!);
            File.AppendAllText(UpdateLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志目录不可写时，更新器仍需返回明确的失败结果。
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RuleProxy", CurrentVersion.ToString(3)));
        return client;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return file.Length >= 2 && file.ReadByte() == 'M' && file.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryGetTrustedAssetUrl(string? value, out string url)
    {
        url = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = parsed.ToString();
        return true;
    }

    private static bool TryGetSha256(string? value, out string sha256)
    {
        sha256 = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? value[7..]
            : value;
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            return false;
        }

        sha256 = normalized.ToLowerInvariant();
        return true;
    }

    private static bool MatchesSha256(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(expected));
        }
        catch (IOException)
        {
            return false;
        }
    }
}