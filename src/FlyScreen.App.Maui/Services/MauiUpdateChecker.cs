using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.AppMaui.Services;

/// <summary>检查 GitHub Release（对齐 Win GitHubUpdateService.CheckAsync）；自动更新引导打开 Releases。</summary>
public sealed class MauiUpdateChecker
{
    public const string Owner = "sclshu3714";
    public const string Repo = "FlyScreen";
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XuanNiao-FlyScreen-Updater");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version GetLocalVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version
                ?? Assembly.GetExecutingAssembly().GetName().Version;
        return v ?? new Version(1, 0, 0, 0);
    }

    public static string FormatVersion(Version v)
    {
        var patch = Math.Max(v.Build, 0);
        return $"{v.Major}.{v.Minor}.{patch}";
    }

    public async Task<MauiUpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApiUrl, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return MauiUpdateCheckResult.Fail("GitHub 上尚无 Release。请访问：\n" + ReleasesUrl);

            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var remote = ParseVersion(tag);
            if (remote == null)
                return MauiUpdateCheckResult.Fail($"无法解析版本号：{tag}");

            var local = GetLocalVersion();
            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? ReleasesUrl : ReleasesUrl;

            string? assetName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    assetName ??= name;
                    if (name.Contains("win", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        break;
                    }
                }
            }

            var newer = Compare(remote, local) > 0;
            return new MauiUpdateCheckResult
            {
                Success = true,
                UpdateAvailable = newer,
                LocalVersion = FormatVersion(local),
                RemoteVersion = FormatVersion(remote),
                ReleaseNotes = notes.Trim(),
                ReleaseUrl = htmlUrl,
                AssetName = assetName,
                Message = newer
                    ? $"发现新版本 {FormatVersion(remote)}（当前 {FormatVersion(local)}）"
                    : $"已是最新版本（{FormatVersion(local)}）"
            };
        }
        catch (Exception ex)
        {
            return MauiUpdateCheckResult.Fail("检查更新失败：" + ex.Message + "\n也可手动访问：\n" + ReleasesUrl);
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag, @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    private static int Compare(Version a, Version b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        return Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
    }
}

public sealed class MauiUpdateCheckResult
{
    public bool Success { get; init; }
    public bool UpdateAvailable { get; init; }
    public string LocalVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string? AssetName { get; init; }
    public string Message { get; init; } = "";

    public static MauiUpdateCheckResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}
