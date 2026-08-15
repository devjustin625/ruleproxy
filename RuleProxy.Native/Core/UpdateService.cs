using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RuleProxy.Native.Core;

public sealed record UpdateRelease(Version Version, string TagName, string ReleaseUrl, string DownloadUrl);

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/devjustin625/ruleproxy/releases/latest";
    private const string AssetName = "RuleProxy.exe";
    private readonly HttpClient _httpClient;

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

            if (!IsExecutable(temporaryPath))
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
                    asset.TryGetProperty("browser_download_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out _))
                {
                    release = new UpdateRelease(version, tagElement.GetString()!, pageElement.GetString() ?? "", url.GetString()!);
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

    public static void ApplyUpdate(int parentPid, string downloadedExe, string targetExe, bool minimized)
    {
        try
        {
            if (parentPid > 0)
            {
                using var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }

            if (!IsExecutable(downloadedExe) || !Path.IsPathFullyQualified(targetExe)) return;
            var backupDirectory = Path.Combine(Path.GetTempPath(), "RuleProxy", "updates", "backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, Path.GetFileName(targetExe));
            File.Copy(targetExe, backupPath, true);
            try
            {
                File.Copy(downloadedExe, targetExe, true);
            }
            catch
            {
                File.Copy(backupPath, targetExe, true);
                return;
            }

            var startInfo = new ProcessStartInfo(targetExe) { UseShellExecute = false };
            if (minimized) startInfo.ArgumentList.Add("--minimized");
            Process.Start(startInfo);
        }
        catch
        {
            // 更新器必须静默失败，避免影响已退出的主程序。
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
}