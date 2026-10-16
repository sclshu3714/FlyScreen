using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Services.Update;

/// <summary>GitHub Releases：按 <see cref="UpdateChannel"/> 检查并下载本端资源。</summary>
public sealed class GitHubReleaseClient
{
    public const string Owner = "sclshu3714";
    public const string Repo = "FlyScreen";
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XuanNiao-FlyScreen-Updater");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version GetLocalVersion(Assembly? assembly = null)
    {
        var v = (assembly ?? Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        return v ?? new Version(1, 0, 0, 0);
    }

    public static string FormatVersion(Version v)
    {
        var patch = Math.Max(v.Build, 0);
        return $"{v.Major}.{v.Minor}.{patch}";
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        Assembly? localAssembly = null,
        CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApiUrl, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return UpdateCheckResult.Fail("GitHub 上尚无 Release。请访问：\n" + ReleasesUrl);

            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var remote = ParseVersion(tag);
            if (remote == null)
                return UpdateCheckResult.Fail($"无法解析版本号：{tag}");

            var local = GetLocalVersion(localAssembly);
            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? ReleasesUrl : ReleasesUrl;

            string? assetName = null;
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                    if (!UpdateChannelRules.IsPreferredAsset(channel, name)) continue;
                    assetName = name;
                    assetUrl = url;
                    break;
                }
            }

            var newer = Compare(remote, local) > 0;
            var rid = UpdateChannelRules.Describe(channel);
            var msg = newer
                ? $"发现新版本 {FormatVersion(remote)}（当前 {FormatVersion(local)}）"
                : $"已是最新版本（{FormatVersion(local)}）";
            if (newer && assetName == null)
                msg += $"\n本端包（*{rid}*）尚未上传到该 Release，请打开发布页手动选择。";

            return new UpdateCheckResult
            {
                Success = true,
                UpdateAvailable = newer,
                LocalVersion = FormatVersion(local),
                RemoteVersion = FormatVersion(remote),
                TagName = tag,
                ReleaseNotes = notes.Trim(),
                ReleaseUrl = htmlUrl,
                AssetName = assetName,
                AssetDownloadUrl = assetUrl,
                Message = msg
            };
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail("检查更新失败：" + ex.Message + "\n也可手动访问：\n" + ReleasesUrl);
        }
    }

    public async Task DownloadAssetAsync(
        string url,
        string destPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(destPath);

        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        int read;
        var lastReport = DateTime.UtcNow;

        progress?.Report(new UpdateDownloadProgress
        {
            Percent = 0,
            BytesReceived = 0,
            TotalBytes = total,
            Message = total > 0 ? "正在下载更新…" : "正在下载更新（大小未知）…"
        });

        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds < 80 && total > 0 && readTotal < total)
                continue;
            lastReport = now;
            progress?.Report(new UpdateDownloadProgress
            {
                Percent = total > 0 ? Math.Min(99.9, readTotal * 100.0 / total) : -1,
                BytesReceived = readTotal,
                TotalBytes = total,
                Message = total > 0
                    ? $"正在下载更新… {readTotal * 100.0 / total:0}%（{FormatBytes(readTotal)} / {FormatBytes(total)}）"
                    : $"正在下载更新… {FormatBytes(readTotal)}"
            });
        }

        progress?.Report(UpdateDownloadProgress.Complete("下载完成，正在准备安装…"));
    }

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag, @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v)
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0))
            : null;
    }

    public static int Compare(Version a, Version b)
    {
        var na = new Version(a.Major, a.Minor, Math.Max(a.Build, 0), Math.Max(a.Revision, 0));
        var nb = new Version(b.Major, b.Minor, Math.Max(b.Build, 0), Math.Max(b.Revision, 0));
        return na.CompareTo(nb);
    }

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "update.bin" : name;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }
}
