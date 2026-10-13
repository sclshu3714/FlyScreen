using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Services.Update;

/// <summary>
/// 通过 GitHub Releases 检查 / 下载 / 替换当前程序（适配单文件 exe）。
/// 仓库：sclshu3714/FlyScreen
/// </summary>
public sealed class GitHubUpdateService
{
    public const string Owner = "sclshu3714";
    public const string Repo = "FlyScreen";
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XuanNiao-FlyScreen-Updater");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version GetLocalVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v ?? new Version(1, 0, 0, 0);
    }

    public static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApiUrl, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Fail("GitHub 上尚无 Release。请访问：\n" + ReleasesUrl);
            }

            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var remote = ParseVersion(tag);
            if (remote == null)
                return UpdateCheckResult.Fail($"无法解析版本号：{tag}");

            var local = GetLocalVersion();
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
                    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) continue;
                    if (IsPreferredAsset(name))
                    {
                        assetName = name;
                        assetUrl = url;
                        break;
                    }

                    assetName ??= name;
                    assetUrl ??= url;
                }
            }

            var newer = remote > Normalize(local);
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
                Message = newer
                    ? $"发现新版本 {FormatVersion(remote)}（当前 {FormatVersion(local)}）"
                    : $"已是最新版本（{FormatVersion(local)}）"
            };
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail("检查更新失败：" + ex.Message + "\n也可手动访问：\n" + ReleasesUrl);
        }
    }

    /// <summary>
    /// 下载最新 Release 资源并替换当前 exe，成功后退出进程由脚本拉起新版本。
    /// </summary>
    public async Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!check.Success || !check.UpdateAvailable)
            return UpdateApplyResult.Fail("没有可用更新。");
        if (string.IsNullOrWhiteSpace(check.AssetDownloadUrl))
            return UpdateApplyResult.Fail("Release 中没有可下载的安装包。请打开：\n" + check.ReleaseUrl);

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return UpdateApplyResult.Fail("无法定位当前程序路径。");

        var tempDir = Path.Combine(Path.GetTempPath(), "XuanNiaoUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var assetName = string.IsNullOrWhiteSpace(check.AssetName) ? "update.bin" : check.AssetName;
            var downloadPath = Path.Combine(tempDir, assetName);

            await DownloadFileAsync(check.AssetDownloadUrl!, downloadPath, progress, ct).ConfigureAwait(false);

            var newExe = ResolveExeFromDownload(downloadPath, tempDir);
            if (newExe == null)
                return UpdateApplyResult.Fail("下载包中未找到可执行文件（.exe）。");

            var stagingExe = Path.Combine(tempDir, "FlyScreen.new.exe");
            File.Copy(newExe, stagingExe, overwrite: true);

            var bat = Path.Combine(tempDir, "apply-update.bat");
            var pid = Environment.ProcessId;
            await File.WriteAllTextAsync(bat,
                "@echo off\r\n" +
                "setlocal\r\n" +
                $":wait\r\n" +
                $"timeout /t 1 /nobreak >nul\r\n" +
                $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
                "if not errorlevel 1 goto wait\r\n" +
                $"copy /Y \"{stagingExe}\" \"{currentExe}\"\r\n" +
                $"if errorlevel 1 (\r\n" +
                $"  ping 127.0.0.1 -n 2 >nul\r\n" +
                $"  copy /Y \"{stagingExe}\" \"{currentExe}\"\r\n" +
                $")\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                $"rd /s /q \"{tempDir}\"\r\n",
                ct).ConfigureAwait(false);

            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempDir
            });

            return UpdateApplyResult.Ok("正在安装更新并重启…");
        }
        catch (Exception ex)
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            return UpdateApplyResult.Fail("下载或准备更新失败：" + ex.Message);
        }
    }

    private static async Task DownloadFileAsync(
        string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[82 * 1024];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;
            if (total > 0)
                progress?.Report(readTotal * 100.0 / total);
        }

        progress?.Report(100);
    }

    private static string? ResolveExeFromDownload(string downloadPath, string tempDir)
    {
        if (downloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return downloadPath;

        if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(tempDir, "extract");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
            var exes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
            var preferred = exes.FirstOrDefault(f =>
                Path.GetFileName(f).Equals("FlyScreen.exe", StringComparison.OrdinalIgnoreCase));
            return preferred ?? exes.FirstOrDefault();
        }

        return null;
    }

    private static bool IsPreferredAsset(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("arm")) return false;
        if (n.EndsWith(".exe") && (n.Contains("flyscreen") || n.Contains("xuanniao") || n.Contains("win-x64")))
            return true;
        if (n.EndsWith(".zip") && (n.Contains("win-x64") || n.Contains("flyscreen")))
            return true;
        return n is "flyscreen.exe";
    }

    private static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag, @"\d+(\.\d+){1,3}");
        if (!m.Success) return null;
        return Version.TryParse(m.Value, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

public sealed class UpdateCheckResult
{
    public bool Success { get; init; }
    public bool UpdateAvailable { get; init; }
    public string LocalVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public string TagName { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string? AssetName { get; init; }
    public string? AssetDownloadUrl { get; init; }
    public string Message { get; init; } = "";

    public static UpdateCheckResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}

public sealed class UpdateApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool ShouldExit { get; init; }

    public static UpdateApplyResult Ok(string message) => new()
    {
        Success = true,
        ShouldExit = true,
        Message = message
    };

    public static UpdateApplyResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}
