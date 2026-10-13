using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyScreen.Services.Update;

/// <summary>
/// 通过 GitHub Releases 检查 / 下载 / 替换当前程序（适配单文件自包含 exe）。
/// </summary>
public sealed class GitHubUpdateService
{
    public const string Owner = "sclshu3714";
    public const string Repo = "FlyScreen";
    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    public static string UpdateLogPath => Path.Combine(Path.GetTempPath(), "xuanniao-update.log");

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

    public static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

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

            var newer = Compare(remote, local) > 0;
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

        var currentExe = ResolveRunningExePath();
        if (currentExe == null)
            return UpdateApplyResult.Fail("无法定位当前程序路径（ProcessPath）。");

        var tempDir = Path.Combine(Path.GetTempPath(), "XuanNiaoUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            try { File.WriteAllText(UpdateLogPath, $"[{DateTime.Now:o}] start update → {currentExe}\r\n"); }
            catch { /* ignore */ }

            var assetName = string.IsNullOrWhiteSpace(check.AssetName) ? "update.bin" : check.AssetName;
            var downloadPath = Path.Combine(tempDir, SanitizeFileName(assetName));

            await DownloadFileAsync(check.AssetDownloadUrl!, downloadPath, progress, ct).ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Indeterminate("正在解压/准备安装包…"));
            var newExe = ResolveExeFromDownload(downloadPath, tempDir);
            if (newExe == null)
                return UpdateApplyResult.Fail("下载包中未找到可执行文件（.exe）。");

            var stagingExe = Path.Combine(tempDir, "FlyScreen.new.exe");
            File.Copy(newExe, stagingExe, overwrite: true);

            // 关键原因：Release 标签升了，但上传的仍是旧 Version 的 exe → 装完还提示更新
            var local = GetLocalVersion();
            var fileVer = TryReadExeVersion(stagingExe);
            if (fileVer != null && Compare(fileVer, local) <= 0)
            {
                return UpdateApplyResult.Fail(
                    $"下载的 exe 文件版本为 {FormatVersion(fileVer)}，不高于当前 {FormatVersion(local)}。\n" +
                    $"GitHub 标签为 {check.TagName}，但资源里的程序集版本未递增。\n" +
                    "请重新 publish 自包含包，在 csproj 中提高 <Version>，再上传到 Release。\n" +
                    $"目标路径：{currentExe}");
            }

            var expectedRemote = ParseVersion(check.RemoteVersion) ?? ParseVersion(check.TagName);
            if (fileVer != null && expectedRemote != null && Compare(fileVer, expectedRemote) < 0)
            {
                return UpdateApplyResult.Fail(
                    $"下载的 exe 版本 {FormatVersion(fileVer)} 低于 Release 标签 {check.TagName}。\n" +
                    "请确认上传的是本次构建的自包含 FlyScreen.exe。\n" +
                    $"目标路径：{currentExe}");
            }

            var ps1 = Path.Combine(tempDir, "apply-update.ps1");
            await File.WriteAllTextAsync(ps1, BuildApplyPowerShell(), new UTF8Encoding(false), ct)
                .ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Complete("即将退出并安装，请稍候…"));

            var args =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" " +
                $"-WaitPid {Environment.ProcessId} " +
                $"-Source \"{stagingExe}\" " +
                $"-Target \"{currentExe}\" " +
                $"-Log \"{UpdateLogPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempDir,
                CreateNoWindow = true
            };

            // 当前若以管理员运行，脚本也提权，避免无权限写入安装目录
            if (IsProcessElevated())
                psi.Verb = "runas";

            Process? started;
            try
            {
                started = Process.Start(psi);
            }
            catch (Exception ex) when (psi.Verb == "runas")
            {
                // 用户取消 UAC 时退回非提权尝试
                psi.Verb = null;
                AppendLog($"runas failed: {ex.Message}, fallback");
                started = Process.Start(psi);
            }

            if (started == null)
                return UpdateApplyResult.Fail("无法启动更新脚本。");

            return UpdateApplyResult.Ok(
                $"正在关闭并替换：\n{currentExe}\n" +
                (fileVer != null ? $"新版本文件：{FormatVersion(fileVer)}\n" : "") +
                $"若失败请查看日志：{UpdateLogPath}");
        }
        catch (Exception ex)
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            return UpdateApplyResult.Fail("下载或准备更新失败：" + ex.Message);
        }
    }

    private static string BuildApplyPowerShell() => """
param(
  [Parameter(Mandatory=$true)][int]$WaitPid,
  [Parameter(Mandatory=$true)][string]$Source,
  [Parameter(Mandatory=$true)][string]$Target,
  [Parameter(Mandatory=$true)][string]$Log
)
function Write-Log([string]$m) {
  $line = "[{0}] {1}" -f (Get-Date -Format o), $m
  Add-Content -LiteralPath $Log -Value $line -Encoding UTF8
}
$ErrorActionPreference = 'Stop'
Write-Log "apply begin pid=$WaitPid"
Write-Log "source=$Source"
Write-Log "target=$Target"

try {
  Wait-Process -Id $WaitPid -Timeout 180 -ErrorAction SilentlyContinue
} catch {}
Start-Sleep -Seconds 2

if (-not (Test-Path -LiteralPath $Source)) {
  Write-Log "FAIL source missing"
  exit 2
}

$old = "$Target.old"
$ok = $false
for ($i = 1; $i -le 60; $i++) {
  try {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $Target) {
      Move-Item -LiteralPath $Target -Destination $old -Force
    }
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    $srcLen = (Get-Item -LiteralPath $Source).Length
    $dstLen = (Get-Item -LiteralPath $Target).Length
    if ($srcLen -ne $dstLen) { throw "size mismatch src=$srcLen dst=$dstLen" }
    Write-Log "OK replaced size=$dstLen try=$i"
    $ok = $true
    break
  } catch {
    Write-Log "retry $i : $($_.Exception.Message)"
    Start-Sleep -Seconds 1
  }
}

if (-not $ok) {
  Write-Log "FAIL replace"
  if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $Target)) {
    Move-Item -LiteralPath $old -Destination $Target -Force -ErrorAction SilentlyContinue
  }
  if (Test-Path -LiteralPath $Target) { Start-Process -FilePath $Target }
  exit 1
}

Start-Process -FilePath $Target
Start-Sleep -Seconds 2
Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
Write-Log "done restart"
exit 0
""";

    private static void AppendLog(string message)
    {
        try { File.AppendAllText(UpdateLogPath, $"[{DateTime.Now:o}] {message}\r\n"); }
        catch { /* ignore */ }
    }

    /// <summary>解析用户真正启动的那个 exe，避开 Temp\.net 解压缓存。</summary>
    public static string? ResolveRunningExePath()
    {
        var list = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                list.Add(Environment.ProcessPath);
        }
        catch { /* ignore */ }

        try
        {
            var mod = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mod))
                list.Add(mod);
        }
        catch { /* ignore */ }

        foreach (var raw in list)
        {
            try
            {
                var full = Path.GetFullPath(raw);
                if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(full)) continue;
                if (IsDotNetExtractPath(full)) continue;
                return full;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private static bool IsDotNetExtractPath(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Contains(@"\Temp\.net\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\AppData\Local\Temp\.net\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static Version? TryReadExeVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return ParseVersion(info.FileVersion)
                   ?? ParseVersion(info.ProductVersion)
                   ?? (info.FileMajorPart > 0
                       ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                       : null);
        }
        catch
        {
            return null;
        }
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

    private static int Compare(Version a, Version b)
        => Normalize(a).CompareTo(Normalize(b));

    private static Version? ParseVersion(string? tag)
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
