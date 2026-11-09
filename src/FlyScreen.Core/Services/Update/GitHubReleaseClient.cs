using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Services.Update;

/// <summary>
/// Release 检查/下载：优先 GitHub，失败后自动改用 Gitee（国内镜像，与 GitHub 同步）。
/// </summary>
public sealed class GitHubReleaseClient
{
    public const string Owner = "sclshu3714";
    public const string Repo = "FlyScreen";

    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public static string GiteeReleasesUrl => $"https://gitee.com/{Owner}/{Repo}/releases";
    public static string GiteeRepoUrl => $"https://gitee.com/{Owner}/{Repo}.git";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    public static string GiteeLatestApiUrl => $"https://gitee.com/api/v5/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XuanNiao-FlyScreen-Updater");
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
        CancellationToken ct = default,
        bool? preferInstallerPackage = null)
    {
        var preferSetup = preferInstallerPackage ?? AppInstallLayout.PreferInstallerPackageAsset();
        var local = GetLocalVersion(localAssembly);
        Exception? githubError = null;

        // GitHub 检查用短超时，国内不通时尽快切 Gitee
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(12));
            var gh = await FetchLatestAsync(
                    LatestApiUrl,
                    ReleasesUrl,
                    "GitHub",
                    githubAccept: true,
                    checkCts.Token)
                .ConfigureAwait(false);
            return BuildCheckResult(gh, channel, local, preferSetup, source: "GitHub", fallbackNote: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested) throw;
            githubError = ex;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            githubError = new TimeoutException("连接 GitHub 超时");
        }

        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(15));
            var ge = await FetchLatestAsync(
                    GiteeLatestApiUrl,
                    GiteeReleasesUrl,
                    "Gitee",
                    githubAccept: false,
                    checkCts.Token)
                .ConfigureAwait(false);
            var note = "GitHub 不可用，已改用 Gitee。";
            if (githubError != null)
                note += "（" + ShortError(githubError) + "）";
            return BuildCheckResult(ge, channel, local, preferSetup, source: "Gitee", fallbackNote: note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested) throw;
            var detail = githubError == null
                ? ShortError(ex)
                : $"GitHub：{ShortError(githubError)}；Gitee：{ShortError(ex)}";
            return UpdateCheckResult.Fail(
                "检查更新失败：" + detail + "\n也可手动访问：\n" + ReleasesUrl + "\n或\n" + GiteeReleasesUrl);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var detail = githubError == null
                ? "连接 Gitee 超时"
                : $"GitHub：{ShortError(githubError)}；Gitee：连接超时";
            return UpdateCheckResult.Fail(
                "检查更新失败：" + detail + "\n也可手动访问：\n" + ReleasesUrl + "\n或\n" + GiteeReleasesUrl);
        }
    }

    public async Task DownloadAssetAsync(
        string url,
        string destPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Exception? first = null;
        try
        {
            await DownloadOnceAsync(url, destPath, progress, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested) throw;
            first = ex;
        }

        var alt = AlternateDownloadUrl(url);
        if (alt == null || string.Equals(alt, url, StringComparison.OrdinalIgnoreCase))
            throw first ?? new InvalidOperationException("下载失败");

        progress?.Report(UpdateDownloadProgress.Indeterminate(
            first == null ? "正在切换镜像下载…" : "主源下载失败，正在改用镜像…"));
        await DownloadOnceAsync(alt, destPath, progress, ct).ConfigureAwait(false);
    }

    private static async Task DownloadOnceAsync(
        string url,
        string destPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
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

    private sealed class LatestReleaseDto
    {
        public string Tag { get; init; } = "";
        public string Notes { get; init; } = "";
        public string HtmlUrl { get; init; } = "";
        public List<(string Name, string Url)> Assets { get; init; } = new();
    }

    private static async Task<LatestReleaseDto> FetchLatestAsync(
        string apiUrl,
        string releasesPageUrl,
        string sourceName,
        bool githubAccept,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (githubAccept)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        else
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{sourceName} 上尚无 Release。请访问：\n{releasesPageUrl}");

        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException($"{sourceName} Release 缺少 tag_name");

        var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        var htmlUrl = root.TryGetProperty("html_url", out var urlEl)
            ? urlEl.GetString() ?? releasesPageUrl
            : releasesPageUrl;
        if (string.IsNullOrWhiteSpace(htmlUrl))
            htmlUrl = $"{releasesPageUrl.TrimEnd('/')}/tag/{tag}";

        var assets = new List<(string Name, string Url)>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                if (IsSourceArchiveAsset(name, tag)) continue;
                assets.Add((name, url));
            }
        }

        return new LatestReleaseDto
        {
            Tag = tag,
            Notes = notes,
            HtmlUrl = htmlUrl,
            Assets = assets
        };
    }

    private static UpdateCheckResult BuildCheckResult(
        LatestReleaseDto release,
        UpdateChannel channel,
        Version local,
        bool preferSetup,
        string source,
        string? fallbackNote)
    {
        var remote = ParseVersion(release.Tag);
        if (remote == null)
            return UpdateCheckResult.Fail($"无法解析版本号：{release.Tag}");

        string? assetName = null;
        string? assetUrl = null;
        foreach (var (name, url) in release.Assets)
        {
            if (!UpdateChannelRules.IsPreferredAsset(channel, name, preferSetup)) continue;
            assetName = name;
            assetUrl = url;
            break;
        }

        if (assetName == null && preferSetup)
        {
            foreach (var (name, url) in release.Assets)
            {
                if (!UpdateChannelRules.IsPreferredAsset(channel, name, preferInstallerPackage: false))
                    continue;
                assetName = name;
                assetUrl = url;
                break;
            }
        }

        // Gitee 仅同步标签时可能没有安装包资源：按约定文件名拼下载地址再试
        if (assetName == null)
        {
            var guessed = GuessAsset(channel, FormatVersion(remote), preferSetup);
            if (guessed != null)
            {
                assetName = guessed;
                assetUrl = BuildGiteeAssetUrl(release.Tag, guessed);
            }
        }

        var newer = Compare(remote, local) > 0;
        var rid = UpdateChannelRules.Describe(channel);
        var modeHint = preferSetup ? "安装包" : "单文件";
        var msg = newer
            ? $"发现新版本 {FormatVersion(remote)}（当前 {FormatVersion(local)}，{modeHint}，来源 {source}）"
            : $"已是最新版本（{FormatVersion(local)}，{modeHint}，来源 {source}）";
        if (!string.IsNullOrWhiteSpace(fallbackNote))
            msg = fallbackNote + "\n" + msg;

        if (newer && assetName == null)
        {
            msg += preferSetup
                ? $"\n本端安装包（*{rid}*-Setup.exe）尚未上传到该 Release，请打开发布页手动选择。"
                : $"\n本端包（*{rid}*）尚未上传到该 Release，请打开发布页手动选择。";
        }
        else if (newer && preferSetup
                 && assetName != null
                 && !assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase))
        {
            msg += "\n未找到 Setup 安装包，将按单文件方式更新。";
        }

        return new UpdateCheckResult
        {
            Success = true,
            UpdateAvailable = newer,
            LocalVersion = FormatVersion(local),
            RemoteVersion = FormatVersion(remote),
            TagName = release.Tag,
            ReleaseNotes = release.Notes.Trim(),
            ReleaseUrl = release.HtmlUrl,
            AssetName = assetName,
            AssetDownloadUrl = assetUrl,
            UpdateSource = source,
            Message = msg
        };
    }

    /// <summary>跳过 Gitee/GitHub 随 tag 自动附带的源码包。</summary>
    private static bool IsSourceArchiveAsset(string name, string tag)
    {
        var n = name.Trim();
        var lower = n.ToLowerInvariant();
        if (!(lower.EndsWith(".zip") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")))
            return false;

        var tagLeaf = tag.Trim().TrimStart('v', 'V');
        if (string.Equals(n, tag + ".zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, tag + ".tar.gz", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, tagLeaf + ".zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, tagLeaf + ".tar.gz", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "source.zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "source.tar.gz", StringComparison.OrdinalIgnoreCase))
            return true;

        // Gitee：archive/refs/tags/... 生成的资源名通常就是 tag.zip
        return !lower.Contains("flyscreen") && !lower.Contains("xuanniao");
    }

    private static string? GuessAsset(UpdateChannel channel, string version, bool preferSetup)
    {
        return channel switch
        {
            UpdateChannel.WinWpf => preferSetup
                ? $"FlyScreen-{version}-win-x64-Setup.exe"
                : $"FlyScreen-{version}-win-x64.exe",
            UpdateChannel.MauiWin => preferSetup
                ? $"FlyScreen-{version}-maui-win-x64-Setup.exe"
                : $"FlyScreen-{version}-maui-win-x64.exe",
            UpdateChannel.MauiAndroid => $"FlyScreen-{version}-maui-android.apk",
            UpdateChannel.MauiMac => $"FlyScreen-{version}-maui-osx-arm64",
            UpdateChannel.DesktopMac =>
                $"FlyScreen-{version}-{UpdateChannelRules.Describe(UpdateChannel.DesktopMac)}",
            UpdateChannel.DesktopLinux => preferSetup
                ? $"FlyScreen-{version}-{UpdateChannelRules.Describe(UpdateChannel.DesktopLinux)}.tar.gz"
                : $"FlyScreen-{version}-{UpdateChannelRules.Describe(UpdateChannel.DesktopLinux)}",
            _ => null
        };
    }

    private static string BuildGiteeAssetUrl(string tag, string fileName)
        => $"https://gitee.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";

    private static string BuildGitHubAssetUrl(string tag, string fileName)
        => $"https://github.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";

    /// <summary>GitHub ↔ Gitee 同路径资源互为镜像。</summary>
    public static string? AlternateDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath; // /owner/repo/releases/download/tag/file
            var m = Regex.Match(path, @"^/[^/]+/[^/]+/releases/download/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            var file = Uri.UnescapeDataString(m.Groups[2].Value);

            if (uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                return BuildGiteeAssetUrl(tag, file);
            if (uri.Host.Contains("gitee", StringComparison.OrdinalIgnoreCase))
                return BuildGitHubAssetUrl(tag, file);
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string ShortError(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(msg)) msg = ex.GetType().Name;
        return msg.Length <= 120 ? msg : msg[..117] + "…";
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
