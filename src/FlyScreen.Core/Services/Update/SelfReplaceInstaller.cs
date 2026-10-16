using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace FlyScreen.Services.Update;

/// <summary>
/// 桌面单文件自替换安装器（Win exe / Unix 可执行文件）。
/// 下载本端资源 → 等待进程退出 → 覆盖 → 重启。
/// </summary>
public sealed class SelfReplaceInstaller
{
    public static string UpdateLogPath => Path.Combine(Path.GetTempPath(), "xuanniao-update.log");

    private readonly GitHubReleaseClient _client = new();

    public async Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!check.Success || !check.UpdateAvailable)
            return UpdateApplyResult.Fail("没有可用更新。");
        if (string.IsNullOrWhiteSpace(check.AssetDownloadUrl))
            return UpdateApplyResult.Fail("Release 中没有可下载的本端安装包。请打开：\n" + check.ReleaseUrl);

        var current = ResolveRunningBinaryPath();
        if (current == null)
            return UpdateApplyResult.Fail("无法定位当前程序路径。");

        var tempDir = Path.Combine(Path.GetTempPath(), "XuanNiaoUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            try { File.WriteAllText(UpdateLogPath, $"[{DateTime.Now:o}] start update → {current}\r\n"); }
            catch { /* ignore */ }

            var assetName = string.IsNullOrWhiteSpace(check.AssetName) ? "update.bin" : check.AssetName;
            var downloadPath = Path.Combine(tempDir, GitHubReleaseClient.SanitizeFileName(assetName));
            await _client.DownloadAssetAsync(check.AssetDownloadUrl!, downloadPath, progress, ct)
                .ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Indeterminate("正在解压/准备安装包…"));
            var newBin = ResolveBinaryFromDownload(downloadPath, tempDir, Path.GetFileName(current));
            if (newBin == null)
                return UpdateApplyResult.Fail("下载包中未找到可执行文件。");

            var staging = Path.Combine(tempDir, "FlyScreen.new" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.Copy(newBin, staging, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/chmod",
                        Arguments = $"+x \"{staging}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit(5000);
                }
                catch { /* ignore */ }
            }

            progress?.Report(UpdateDownloadProgress.Complete("即将退出并安装，请稍候…"));

            if (OperatingSystem.IsWindows())
                return LaunchWindowsApply(staging, current, tempDir);
            return LaunchUnixApply(staging, current, tempDir);
        }
        catch (Exception ex)
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            return UpdateApplyResult.Fail("下载或准备更新失败：" + ex.Message);
        }
    }

    private static UpdateApplyResult LaunchWindowsApply(string staging, string target, string tempDir)
    {
        var ps1 = Path.Combine(tempDir, "apply-update.ps1");
        File.WriteAllText(ps1, BuildWindowsApplyScript(), new UTF8Encoding(false));
        var args =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" " +
            $"-WaitPid {Environment.ProcessId} " +
            $"-Source \"{staging}\" " +
            $"-Target \"{target}\" " +
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
        if (OperatingSystem.IsWindows() && IsProcessElevated())
            psi.Verb = "runas";

        try
        {
            if (Process.Start(psi) == null)
                return UpdateApplyResult.Fail("无法启动更新脚本。");
        }
        catch (Exception) when (psi.Verb == "runas")
        {
            psi.Verb = null;
            try
            {
                if (Process.Start(psi) == null)
                    return UpdateApplyResult.Fail("无法启动更新脚本。");
            }
            catch (Exception ex2)
            {
                return UpdateApplyResult.Fail("无法启动更新脚本：" + ex2.Message);
            }
        }
        catch (Exception ex)
        {
            return UpdateApplyResult.Fail("无法启动更新脚本：" + ex.Message);
        }

        return UpdateApplyResult.Ok(
            $"正在关闭并替换：\n{target}\n若失败请查看日志：{UpdateLogPath}");
    }

    private static UpdateApplyResult LaunchUnixApply(string staging, string target, string tempDir)
    {
        var sh = Path.Combine(tempDir, "apply-update.sh");
        File.WriteAllText(sh, BuildUnixApplyScript(), new UTF8Encoding(false));
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/chmod",
                Arguments = $"+x \"{sh}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(5000);
        }
        catch { /* ignore */ }

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{sh}\" {Environment.ProcessId} \"{staging}\" \"{target}\" \"{UpdateLogPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDir
        };

        try
        {
            if (Process.Start(psi) == null)
                return UpdateApplyResult.Fail("无法启动更新脚本。");
        }
        catch (Exception ex)
        {
            return UpdateApplyResult.Fail("无法启动更新脚本：" + ex.Message);
        }

        return UpdateApplyResult.Ok(
            $"正在关闭并替换：\n{target}\n若失败请查看日志：{UpdateLogPath}");
    }

    public static string? ResolveRunningBinaryPath()
    {
        var candidates = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                candidates.Add(Environment.ProcessPath);
        }
        catch { /* ignore */ }

        try
        {
            var mod = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mod))
                candidates.Add(mod);
        }
        catch { /* ignore */ }

        foreach (var raw in candidates)
        {
            try
            {
                var full = Path.GetFullPath(raw);
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
        var p = path.Replace('\\', '/');
        return p.Contains("/Temp/.net/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/tmp/.net/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveBinaryFromDownload(string downloadPath, string tempDir, string preferredName)
    {
        if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(tempDir, "extract");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
            var files = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            var preferred = files.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;
            preferred = files.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith("FlyScreen", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            return preferred ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        // 单文件：exe / 无扩展名 / apk 以外的桌面包
        if (downloadPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            return null;
        return downloadPath;
    }

    private static string BuildWindowsApplyScript() => """
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
try { Wait-Process -Id $WaitPid -Timeout 180 -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 2
if (-not (Test-Path -LiteralPath $Source)) { Write-Log "FAIL source missing"; exit 2 }
$old = "$Target.old"
$ok = $false
for ($i = 1; $i -le 60; $i++) {
  try {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $Target) { Move-Item -LiteralPath $Target -Destination $old -Force }
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    Write-Log "OK replaced try=$i"
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

    private static string BuildUnixApplyScript() => """
#!/usr/bin/env bash
set -euo pipefail
WAIT_PID="$1"
SOURCE="$2"
TARGET="$3"
LOG="$4"
log() { echo "[$(date -Iseconds)] $*" >> "$LOG"; }
log "apply begin pid=$WAIT_PID"
for i in $(seq 1 180); do
  if ! kill -0 "$WAIT_PID" 2>/dev/null; then break; fi
  sleep 1
done
sleep 2
if [ ! -f "$SOURCE" ]; then log "FAIL source missing"; exit 2; fi
OLD="${TARGET}.old"
ok=0
for i in $(seq 1 60); do
  rm -f "$OLD" || true
  if [ -f "$TARGET" ]; then mv -f "$TARGET" "$OLD" || true; fi
  if cp -f "$SOURCE" "$TARGET" && chmod +x "$TARGET"; then
    log "OK replaced try=$i"
    ok=1
    break
  fi
  log "retry $i"
  sleep 1
done
if [ "$ok" -ne 1 ]; then
  log "FAIL replace"
  if [ -f "$OLD" ] && [ ! -f "$TARGET" ]; then mv -f "$OLD" "$TARGET" || true; fi
  if [ -f "$TARGET" ]; then nohup "$TARGET" >/dev/null 2>&1 & fi
  exit 1
fi
nohup "$TARGET" >/dev/null 2>&1 &
sleep 2
rm -f "$OLD" "$SOURCE" || true
log "done restart"
exit 0
""";
}
