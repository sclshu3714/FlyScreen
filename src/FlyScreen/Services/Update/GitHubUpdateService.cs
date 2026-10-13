using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Services.Update;

/// <summary>
/// 通过 GitHub Releases 检查 / 下载 / 替换当前程序（适配单文件自包含 exe）。
/// 流程：下载到临时目录 → 写外部脚本 → 退出本进程释放文件锁 → 脚本等待 PID 结束后改名替换并重启。
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
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
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
                return UpdateCheckResult.Fail("GitHub 上尚无 Release。请访问：\n" + ReleasesUrl);

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
    /// 下载并准备替换；成功后返回 ShouldExit=true，调用方须退出进程以便脚本覆盖单文件 exe。
    /// </summary>
    public async Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!check.Success || !check.UpdateAvailable)
            return UpdateApplyResult.Fail("没有可用更新。");
        if (string.IsNullOrWhiteSpace(check.AssetDownloadUrl))
            return UpdateApplyResult.Fail("Release 中没有可下载的安装包。请打开：\n" + check.ReleaseUrl);

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return UpdateApplyResult.Fail("无法定位当前程序路径。");

        // 单文件发布时 ProcessPath 即为用户启动的那个 exe
        currentExe = Path.GetFullPath(currentExe);

        var tempDir = Path.Combine(Path.GetTempPath(), "XuanNiaoUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var assetName = string.IsNullOrWhiteSpace(check.AssetName) ? "update.bin" : check.AssetName;
            var downloadPath = Path.Combine(tempDir, SanitizeFileName(assetName));

            await DownloadFileAsync(check.AssetDownloadUrl!, downloadPath, progress, ct).ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Indeterminate("正在解压/准备安装包…"));
            var newExe = ResolveExeFromDownload(downloadPath, tempDir);
            if (newExe == null)
                return UpdateApplyResult.Fail("下载包中未找到可执行文件（.exe）。");

            var stagingExe = Path.Combine(tempDir, "FlyScreen.new.exe");
            File.Copy(newExe, stagingExe, overwrite: true);

            var bat = Path.Combine(tempDir, "apply-update.bat");
            await File.WriteAllTextAsync(bat, BuildApplyScript(Environment.ProcessId, stagingExe, currentExe),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Complete("即将退出并安装，请稍候…"));

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempDir,
                CreateNoWindow = true
            });

            if (started == null)
                return UpdateApplyResult.Fail("无法启动更新脚本。");

            return UpdateApplyResult.Ok("正在关闭程序并替换文件，随后自动重启…");
        }
        catch (Exception ex)
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            return UpdateApplyResult.Fail("下载或准备更新失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 外部 bat：等待本进程退出 → 将旧 exe 改名为 .old → 拷入新 exe → 启动 → 清理。
    /// 单文件 exe 运行中无法直接覆盖，必须先退出；Windows 上通常可对已退出进程的文件 rename。
    /// </summary>
    private static string BuildApplyScript(int pid, string stagingExe, string targetExe)
    {
        var oldExe = targetExe + ".old";
        var source = stagingExe.Replace("\"", "");
        var target = targetExe.Replace("\"", "");
        var old = oldExe.Replace("\"", "");

        return string.Join("\r\n",
            "@echo off",
            "setlocal EnableExtensions",
            $"set \"PID={pid}\"",
            $"set \"SOURCE={source}\"",
            $"set \"TARGET={target}\"",
            $"set \"OLD={old}\"",
            "set TRY=0",
            "",
            "REM 等待主进程退出（释放单文件 exe 锁）",
            $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"try {{ Wait-Process -Id {pid} -Timeout 180 -ErrorAction SilentlyContinue }} catch {{ }}\"",
            "timeout /t 1 /nobreak >nul",
            "",
            ":replace",
            "set /a TRY+=1",
            "if exist \"%OLD%\" del /F /Q \"%OLD%\" >nul 2>&1",
            "if exist \"%TARGET%\" (",
            "  move /Y \"%TARGET%\" \"%OLD%\" >nul 2>&1",
            "  if exist \"%TARGET%\" (",
            "    if %TRY% LSS 45 (",
            "      timeout /t 1 /nobreak >nul",
            "      goto replace",
            "    )",
            "    goto fail",
            "  )",
            ")",
            "",
            "copy /Y \"%SOURCE%\" \"%TARGET%\" >nul",
            "if errorlevel 1 goto fail",
            "",
            "start \"\" \"%TARGET%\"",
            "timeout /t 2 /nobreak >nul",
            "del /F /Q \"%OLD%\" >nul 2>&1",
            "del /F /Q \"%SOURCE%\" >nul 2>&1",
            "exit /b 0",
            "",
            ":fail",
            "if exist \"%OLD%\" if not exist \"%TARGET%\" move /Y \"%OLD%\" \"%TARGET%\" >nul 2>&1",
            "if exist \"%TARGET%\" start \"\" \"%TARGET%\"",
            "exit /b 1",
            "");
    }

    private static async Task DownloadFileAsync(
        string url, string path, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);

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
            if ((now - lastReport).TotalMilliseconds < 80 && readTotal < total)
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

        progress?.Report(new UpdateDownloadProgress
        {
            Percent = 100,
            BytesReceived = readTotal,
            TotalBytes = total > 0 ? total : readTotal,
            Message = "下载完成，正在准备安装…"
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "update.bin" : name;
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

public sealed class UpdateDownloadProgress
{
    /// <summary>0–100；未知大小时为 -1（界面可走不确定进度）。</summary>
    public double Percent { get; init; }
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; } = -1;
    public string Message { get; init; } = "";

    public bool IsIndeterminate => Percent < 0;

    public static UpdateDownloadProgress Indeterminate(string message) => new()
    {
        Percent = -1,
        Message = message
    };

    public static UpdateDownloadProgress Complete(string message) => new()
    {
        Percent = 100,
        Message = message
    };
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
