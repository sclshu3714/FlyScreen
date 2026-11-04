using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlyScreen.Uninstaller.Services;

public sealed class UninstallService
{
    public async Task UninstallAsync(
        UninstallContext ctx,
        bool keepUserData,
        IProgress<ProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        Report(progress, 0, "正在检查运行中的程序…");
        await StopAppAsync(ctx, progress, ct).ConfigureAwait(false);
        Report(progress, 12, "已结束相关进程");

        Report(progress, 15, "移除快捷方式…");
        RemoveShortcuts(ctx.ProductName);
        Report(progress, 20, "快捷方式已移除");

        // 20–85% 删除程序文件
        await Task.Run(() => DeleteProgramFiles(ctx.InstallDir, progress, 20, 85, ct), ct)
            .ConfigureAwait(false);

        if (!keepUserData)
        {
            Report(progress, 88, "删除个人配置…");
            await Task.Run(() => DeleteUserData(), ct).ConfigureAwait(false);
            Report(progress, 94, "个人配置已删除");
        }
        else
        {
            Report(progress, 92, "已保留个人配置");
        }

        Report(progress, 96, "清理注册表…");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallContext.UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch { /* ignore */ }

        Report(progress, 100, "卸载完成");
    }

    private static void Report(IProgress<ProgressReport>? progress, double percent, string message)
        => progress?.Report(ProgressReport.Create(percent, message));

    /// <summary>
    /// 若当前进程在安装目录内，复制到临时目录再启动，以便删除自身。
    /// 返回 true 表示已转交临时进程并应立即退出。
    /// </summary>
    public static bool TryRelaunchFromTemp(string[] args, string installDir)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
            return false;

        var fullInstall = Path.GetFullPath(installDir).TrimEnd('\\', '/');
        var selfDir = Path.GetFullPath(Path.GetDirectoryName(self)!);
        if (!selfDir.StartsWith(fullInstall, StringComparison.OrdinalIgnoreCase))
            return false;

        if (args.Any(a => a is "--from-temp" or "/from-temp"))
            return false;

        var tempExe = Path.Combine(Path.GetTempPath(), "FlyScreen-Uninstall-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.Copy(self, tempExe, overwrite: true);

        var argList = new List<string>
        {
            "--from-temp",
            "--install-dir",
            $"\"{fullInstall}\""
        };
        foreach (var a in args)
        {
            if (a is "--from-temp" or "/from-temp") continue;
            if (a is "--install-dir" or "/install-dir") continue;
            argList.Add(a);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = tempExe,
            Arguments = string.Join(' ', argList),
            UseShellExecute = false
        });
        return true;
    }

    public static void ScheduleSelfDelete(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        var cmd = $"ping 127.0.0.1 -n 2 >nul & del /f /q \"{exePath}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/C " + cmd,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static async Task StopAppAsync(UninstallContext ctx, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FlyScreen",
            Path.GetFileNameWithoutExtension(ctx.ExeName)
        };
        names.RemoveWhere(string.IsNullOrWhiteSpace);
        var selfId = Environment.ProcessId;
        var procs = new List<Process>();

        foreach (var name in names)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in list)
            {
                try
                {
                    if (p.Id == selfId || p.HasExited) { p.Dispose(); continue; }
                    if (p.ProcessName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.StartsWith("FlyScreen-Uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Dispose();
                        continue;
                    }
                    procs.Add(p);
                }
                catch { try { p.Dispose(); } catch { /* ignore */ } }
            }
        }

        if (procs.Count == 0) return;
        Report(progress, 4, "正在退出运行中的程序…");

        foreach (var p in procs)
        {
            try { if (!p.HasExited) p.CloseMainWindow(); } catch { /* ignore */ }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (procs.All(p => { try { return p.HasExited; } catch { return true; } }))
                break;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited)
                {
                    Report(progress, 8, $"强制结束 {p.ProcessName}…");
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
            finally { p.Dispose(); }
        }
    }

    private static void DeleteProgramFiles(
        string installDir,
        IProgress<ProgressReport>? progress,
        double fromPercent,
        double toPercent,
        CancellationToken ct)
    {
        if (!Directory.Exists(installDir))
        {
            Report(progress, toPercent, "安装目录不存在");
            return;
        }

        var self = Path.GetFullPath(Environment.ProcessPath ?? "");
        var files = Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories).ToList();
        var total = Math.Max(1, files.Count);
        var lastPct = fromPercent - 1;
        var deleted = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var full = Path.GetFullPath(file);
                if (full.Equals(self, StringComparison.OrdinalIgnoreCase))
                    continue;
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
            }
            catch { /* ignore locked */ }

            deleted++;
            var pct = fromPercent + deleted / (double)total * (toPercent - fromPercent) * 0.9;
            if (pct - lastPct >= 0.5 || deleted == files.Count)
            {
                lastPct = pct;
                Report(progress, pct, $"移除程序文件… {deleted}/{total}");
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(installDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.Delete(dir, recursive: false); } catch { /* ignore */ }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(installDir).Any())
                Directory.Delete(installDir, recursive: false);
            else
            {
                foreach (var f in Directory.EnumerateFiles(installDir))
                {
                    try
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                        File.Delete(f);
                    }
                    catch { /* ignore */ }
                }
                try { Directory.Delete(installDir, recursive: true); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        Report(progress, toPercent, "程序文件已移除");
    }

    private static void DeleteUserData()
    {
        // 个人配置：%AppData%\Roaming\FlyScreen（SpecialFolder.ApplicationData）
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen");
        TryDeleteDir(appData);
    }

    private static void TryDeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch { /* ignore */ }
    }

    private static void RemoveShortcuts(string productName)
    {
        try
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                productName + ".lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
        }
        catch { /* ignore */ }

        try
        {
            var linkDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", productName);
            if (Directory.Exists(linkDir))
                Directory.Delete(linkDir, recursive: true);
        }
        catch { /* ignore */ }
    }
}
