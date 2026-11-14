using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlyScreen.Services.Update;

namespace FlyScreen.Services.Tools;

/// <summary>从 GitHub/Gitee Release 拉取 tools-index.json，下载 .flytool 到 Cache。</summary>
public sealed class ToolRemoteIndexClient
{
    private static readonly HttpClient Http = CreateClient();
    private ToolIndexFile? _cache;
    private DateTimeOffset _cacheAt;

    public async Task<ToolIndexEntry?> FindAsync(string toolId, CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct).ConfigureAwait(false);
        return index?.Tools?.FirstOrDefault(t =>
            string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ToolIndexFile?> GetIndexAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache != null && DateTimeOffset.UtcNow - _cacheAt < TimeSpan.FromMinutes(30))
            return _cache;

        FlyScreenDataPaths.EnsureToolsLayout();

        // 磁盘缓存
        if (!forceRefresh && File.Exists(FlyScreenDataPaths.ToolsIndexCacheFile))
        {
            try
            {
                var disk = await File.ReadAllTextAsync(FlyScreenDataPaths.ToolsIndexCacheFile, ct)
                    .ConfigureAwait(false);
                _cache = JsonSerializer.Deserialize<ToolIndexFile>(disk, ToolPackageJson.Options);
                _cacheAt = DateTimeOffset.UtcNow;
                if (_cache?.Tools?.Count > 0)
                    return _cache;
            }
            catch { /* fall through */ }
        }

        ToolIndexFile? remote = null;
        try
        {
            remote = await FetchIndexAsync(
                $"https://github.com/{GitHubReleaseClient.Owner}/{GitHubReleaseClient.Repo}/releases/latest/download/tools-index.json",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("GitHub tools-index 失败: " + ex.Message);
        }

        if (remote?.Tools == null || remote.Tools.Count == 0)
        {
            try
            {
                remote = await FetchIndexAsync(
                    $"https://gitee.com/{GitHubReleaseClient.Owner}/{GitHubReleaseClient.Repo}/releases/download/latest/tools-index.json",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Gitee tools-index 失败: " + ex.Message);
            }
        }

        if (remote != null)
        {
            _cache = remote;
            _cacheAt = DateTimeOffset.UtcNow;
            try
            {
                await File.WriteAllTextAsync(
                    FlyScreenDataPaths.ToolsIndexCacheFile,
                    JsonSerializer.Serialize(remote, ToolPackageJson.Options),
                    ct).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        return _cache;
    }

    public async Task<string?> DownloadToCacheAsync(ToolIndexEntry entry, CancellationToken ct = default)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var fileName = string.IsNullOrWhiteSpace(entry.FileName)
            ? $"{entry.Id}-{entry.Version}.flytool"
            : entry.FileName.Trim();
        var dest = Path.Combine(FlyScreenDataPaths.ToolsBundledDirectory, fileName);
        if (File.Exists(dest) && !string.IsNullOrWhiteSpace(entry.Sha256))
        {
            var existing = ToolInstaller.Sha256Hex(await File.ReadAllBytesAsync(dest, ct).ConfigureAwait(false));
            if (string.Equals(existing, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return dest;
        }

        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Url)) urls.Add(entry.Url!);
        if (!string.IsNullOrWhiteSpace(entry.GiteeUrl)) urls.Add(entry.GiteeUrl!);
        urls.Add($"https://github.com/{GitHubReleaseClient.Owner}/{GitHubReleaseClient.Repo}/releases/latest/download/{fileName}");
        urls.Add($"https://gitee.com/{GitHubReleaseClient.Owner}/{GitHubReleaseClient.Repo}/releases/download/latest/{fileName}");

        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(entry.Sha256))
                {
                    var hash = ToolInstaller.Sha256Hex(bytes);
                    if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Warn($"工具包哈希不匹配: {fileName}");
                        continue;
                    }
                }

                await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);
                AppLog.Info($"工具包已缓存: {dest}");
                return dest;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"下载工具包失败 {url}: {ex.Message}");
            }
        }

        return File.Exists(dest) ? dest : null;
    }

    private static async Task<ToolIndexFile?> FetchIndexAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<ToolIndexFile>(stream, ToolPackageJson.Options, ct)
            .ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XuanNiao-FlyScreen-Tools");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return c;
    }
}

public sealed class ToolIndexFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<ToolIndexEntry> Tools { get; set; } = [];
}

public sealed class ToolIndexEntry
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? Url { get; set; }
    public string? GiteeUrl { get; set; }
    public string? NameKey { get; set; }
    public string? SummaryKey { get; set; }

    public ToolManifest? ToManifestHint()
    {
        if (string.IsNullOrWhiteSpace(Id)) return null;
        return new ToolManifest
        {
            Id = Id,
            PackageVersion = Version,
            NameKey = NameKey ?? "",
            SummaryKey = SummaryKey ?? ""
        };
    }
}
