using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Uninstaller.Services;

/// <summary>安装包维护：检查 GitHub Release（失败改用 Gitee）并下载对应 *-Setup.exe 覆盖安装。</summary>
public sealed class ModifyUpdateService
{
    private const string Owner = "sclshu3714";
    private const string Repo = "FlyScreen";
    private const string LatestApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
    private const string GiteeLatestApi = "https://gitee.com/api/v5/repos/" + Owner + "/" + Repo + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public sealed record CheckResult(
        bool Success,
        bool UpdateAvailable,
        string LocalVersion,
        string RemoteVersion,
        string? AssetName,
        string? DownloadUrl,
        string Message);

    public CheckResult Check(UninstallContext ctx)
    {
        var local = ReadLocalVersion(ctx);
        Exception? githubError = null;
        try
        {
            return CheckFrom(LatestApi, local, ctx, githubAccept: true, source: "GitHub", fallbackNote: null);
        }
        catch (Exception ex)
        {
            githubError = ex;
        }

        try
        {
            var note = "GitHub 不可用，已改用 Gitee。";
            return CheckFrom(GiteeLatestApi, local, ctx, githubAccept: false, source: "Gitee", fallbackNote: note);
        }
        catch (Exception ex)
        {
            var detail = githubError == null
                ? ex.Message
                : $"GitHub：{githubError.Message}；Gitee：{ex.Message}";
            return new CheckResult(false, false, local, "", null, null, "检查更新失败：" + detail);
        }
    }

    private static CheckResult CheckFrom(
        string api,
        string local,
        UninstallContext ctx,
        bool githubAccept,
        string source,
        string? fallbackNote)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        if (githubAccept)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        else
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = Http.Send(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("HTTP " + (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        var remote = ParseVersion(tag);
        if (remote == null)
            throw new InvalidOperationException("无法解析版本：" + tag);

        var remoteText = Format(remote);
        var preferMaui = IsMauiInstall(ctx);
        string? name = null;
        string? url = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                var u = a.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() : null;
                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(u)) continue;
                if (IsSourceArchive(n, tag)) continue;
                if (!IsSetupForInstall(n, preferMaui)) continue;
                name = n;
                url = u;
                break;
            }
        }

        if (name == null)
        {
            name = preferMaui
                ? $"FlyScreen-{remoteText}-maui-win-x64-Setup.exe"
                : $"FlyScreen-{remoteText}-win-x64-Setup.exe";
            url = $"https://gitee.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
            if (source.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
                url = $"https://github.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        }

        var localVer = ParseVersion(local) ?? new Version(0, 0);
        var newer = remote.CompareTo(localVer) > 0;
        var prefix = string.IsNullOrWhiteSpace(fallbackNote) ? "" : fallbackNote + "\n";
        if (!newer)
        {
            return new CheckResult(true, false, Format(localVer), remoteText, name, url,
                prefix + $"已是最新版本（{Format(localVer)}，来源 {source}）");
        }

        return new CheckResult(true, true, Format(localVer), remoteText, name, url,
            prefix + $"发现新版本 {remoteText}（当前 {Format(localVer)}，来源 {source}）");
    }

    public async Task<string> DownloadAsync(
        string url,
        string fileName,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "XuanNiaoSetupUpdate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Sanitize(fileName));

        try
        {
            await DownloadOnceAsync(url, dest, progress, ct).ConfigureAwait(false);
            return dest;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            var alt = AlternateUrl(url);
            if (alt == null) throw;
            await DownloadOnceAsync(alt, dest, progress, ct).ConfigureAwait(false);
            return dest;
        }
    }

    private static async Task DownloadOnceAsync(
        string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);
        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            readTotal += n;
            if (total > 0)
                progress?.Report(Math.Min(100, readTotal * 100.0 / total));
        }

        progress?.Report(100);
    }

    private static string? AlternateUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var m = Regex.Match(uri.AbsolutePath,
                @"^/[^/]+/[^/]+/releases/download/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            var file = Uri.UnescapeDataString(m.Groups[2].Value);
            if (uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                return $"https://gitee.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(file)}";
            if (uri.Host.Contains("gitee", StringComparison.OrdinalIgnoreCase))
                return $"https://github.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(file)}";
        }
        catch { /* ignore */ }

        return null;
    }

    private static bool IsSourceArchive(string name, string tag)
    {
        var lower = name.ToLowerInvariant();
        if (!(lower.EndsWith(".zip") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")))
            return false;
        if (lower.Contains("flyscreen") || lower.Contains("xuanniao"))
            return false;
        return string.Equals(name, tag + ".zip", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, tag + ".tar.gz", StringComparison.OrdinalIgnoreCase)
               || !lower.Contains("setup");
    }

    public static void LaunchSetup(string setupPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(setupPath) ?? Path.GetTempPath()
        });
    }

    private static string ReadLocalVersion(UninstallContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.Version))
            return ctx.Version.Trim().TrimStart('v', 'V');

        try
        {
            var exe = Path.Combine(ctx.InstallDir, ctx.ExeName);
            if (File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion;
            }
        }
        catch { /* ignore */ }

        return "0.0.0";
    }

    private static bool IsMauiInstall(UninstallContext ctx)
    {
        var name = ctx.ExeName ?? "";
        return name.Contains("Maui", StringComparison.OrdinalIgnoreCase)
               || name.Contains("App.Maui", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSetupForInstall(string fileName, bool maui)
    {
        var n = fileName.ToLowerInvariant();
        if (!n.EndsWith(".exe") || !n.Contains("setup")) return false;
        if (maui)
            return n.Contains("maui-win");
        return n.Contains("win-x64") && !n.Contains("maui");
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\d+(\.\d+){1,3}");
        if (!m.Success || !Version.TryParse(m.Value, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    private static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Setup.exe" : name;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XuanNiao-FlyScreen-Modify");
        return c;
    }
}
