using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlyScreen.Installer.Services;

public sealed class ExistingInstallInfo
{
    public bool IsInstalled { get; init; }
    public string? InstallDir { get; init; }
    public string? ExePath { get; init; }
    public string? Version { get; init; }
}

public sealed class RunningAppInfo
{
    public bool IsRunning { get; init; }
    public IReadOnlyList<int> ProcessIds { get; init; } = [];
}

/// <summary>检测是否已安装、是否正在运行，并尽量正常结束进程。</summary>
public static class InstallProbe
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FlyScreen";

    public static ExistingInstallInfo DetectInstalled(string preferredInstallDir, string exeName)
    {
        string? dir = null;
        string? version = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (key != null)
            {
                dir = key.GetValue("InstallLocation") as string;
                version = key.GetValue("DisplayVersion") as string;
            }
        }
        catch
        {
            // ignore
        }

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            dir = preferredInstallDir;

        var exePath = ResolveExePath(dir, exeName);
        var installed = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);

        // 目录里有主程序，也视为已安装
        if (!installed && Directory.Exists(preferredInstallDir))
        {
            exePath = ResolveExePath(preferredInstallDir, exeName);
            installed = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
            if (installed) dir = preferredInstallDir;
        }

        return new ExistingInstallInfo
        {
            IsInstalled = installed,
            InstallDir = dir,
            ExePath = exePath,
            Version = version
        };
    }

    public static RunningAppInfo DetectRunning(string? installDir, string exeName)
    {
        var ids = new List<int>();
        var names = CandidateProcessNames(exeName);
        var selfId = Environment.ProcessId;

        foreach (var name in names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == selfId) continue;
                    if (p.HasExited) continue;

                    // 若能读到路径，优先匹配安装目录；读不到则按进程名认定
                    var path = TryGetProcessPath(p);
                    if (!string.IsNullOrWhiteSpace(installDir) && !string.IsNullOrWhiteSpace(path))
                    {
                        if (!path.StartsWith(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(Path.GetDirectoryName(path), installDir, StringComparison.OrdinalIgnoreCase))
                        {
                            // 同名但非本安装目录：仍结束（产品主程序通常唯一）
                        }
                    }

                    ids.Add(p.Id);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return new RunningAppInfo
        {
            IsRunning = ids.Count > 0,
            ProcessIds = ids.Distinct().ToList()
        };
    }

    public static async Task StopRunningAsync(
        RunningAppInfo running,
        IProgress<ProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        if (!running.IsRunning) return;

        progress?.Report(ProgressReport.Create(2, "正在关闭已运行的程序…"));
        var procs = new List<Process>();
        foreach (var id in running.ProcessIds)
        {
            try
            {
                var p = Process.GetProcessById(id);
                if (!p.HasExited) procs.Add(p);
            }
            catch
            {
                // already gone
            }
        }

        // 1) 正常关闭主窗口
        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited)
                    p.CloseMainWindow();
            }
            catch
            {
                // ignore
            }
        }

        // 2) 等待退出
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (procs.All(p =>
                {
                    try { return p.HasExited; }
                    catch { return true; }
                }))
                break;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        // 3) 仍未退出则强制结束
        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited)
                {
                    progress?.Report(ProgressReport.Create(5, $"强制结束进程 {p.ProcessName} ({p.Id})…"));
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                p.Dispose();
            }
        }

        // 再扫一遍，避免漏网
        await Task.Delay(300, ct).ConfigureAwait(false);
        var again = DetectRunning(null, "FlyScreen.exe");
        foreach (var id in again.ProcessIds)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string? ResolveExePath(string? dir, string exeName)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return null;

        var preferred = Path.Combine(dir, exeName);
        if (File.Exists(preferred)) return preferred;

        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    return n.Equals("FlyScreen", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("FlyScreen", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidateProcessNames(string exeName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FlyScreen",
            Path.GetFileNameWithoutExtension(exeName)
        };
        return set.Where(s => !string.IsNullOrWhiteSpace(s));
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // 权限不足时走 QueryFullProcessImageName
        }

        try
        {
            return QueryFullProcessImageName(process);
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryFullProcessImageName(Process process)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
                return null;
            return new string(buffer, 0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, char[] lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
