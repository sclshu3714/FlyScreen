using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FlyScreen.Uninstaller.Services;

internal static class ModifyService
{
    public static void RepairShortcuts(string productName, string exePath, string workDir)
    {
        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            productName + ".lnk");
        CreateShortcut(desktop, exePath, workDir, productName);

        var linkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", productName);
        Directory.CreateDirectory(linkDir);
        CreateShortcut(Path.Combine(linkDir, productName + ".lnk"), exePath, workDir, productName);
    }

    public static void RepairUninstallRegistry(UninstallContext ctx, string? exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallContext.UninstallKeyPath);
        if (key == null) return;

        var uninstallExe = Path.Combine(ctx.InstallDir, UninstallContext.UninstallExeName);
        key.SetValue("DisplayName", ctx.ProductName);
        if (!string.IsNullOrWhiteSpace(ctx.Version))
            key.SetValue("DisplayVersion", ctx.Version);
        key.SetValue("Publisher", ctx.ProductName);
        key.SetValue("InstallLocation", ctx.InstallDir);
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            key.SetValue("DisplayIcon", exePath);

        key.SetValue("NoModify", 0);
        key.SetValue("NoRepair", 1);

        if (File.Exists(uninstallExe))
        {
            key.SetValue("UninstallString", $"\"{uninstallExe}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" /S");
            key.SetValue("ModifyPath", $"\"{uninstallExe}\" /modify");
        }

        try
        {
            File.WriteAllText(
                Path.Combine(ctx.InstallDir, ".flyscreen-install"),
                "setup\n",
                Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    public static void RepairPinnedTaskbarShortcuts(string exePath, string workDir, string productName)
    {
        try
        {
            var taskBar = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(taskBar)) return;

            foreach (var lnk in Directory.EnumerateFiles(taskBar, "*.lnk"))
            {
                if (!IsRelatedShortcut(lnk, productName, exePath))
                    continue;
                CreateShortcut(lnk, exePath, workDir, productName);
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 将安装目录迁移到 <paramref name="destDir"/>，并刷新注册表 / 快捷方式 / 任务栏钉住项。
    /// 个人配置（%AppData%）不随安装目录移动。
    /// </summary>
    public static async Task MigrateInstallAsync(
        UninstallContext ctx,
        string destDir,
        IProgress<ProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        var source = Path.GetFullPath(ctx.InstallDir).TrimEnd('\\', '/');
        var dest = Path.GetFullPath(destDir).TrimEnd('\\', '/');

        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标目录与当前安装位置相同。");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("当前安装目录不存在：" + source);
        if (IsSameOrUnder(dest, source))
            throw new InvalidOperationException("目标目录不能位于当前安装目录之内。");
        if (IsSameOrUnder(source, dest))
            throw new InvalidOperationException("目标目录不能是当前安装目录的父路径（会导致文件嵌套）。请选择全新的空目录。");

        Report(progress, 2, "正在退出运行中的程序…");
        await StopProductProcessesAsync(ctx, ct).ConfigureAwait(false);

        Report(progress, 10, "正在复制到新目录…");
        await Task.Run(() => CopyDirectory(source, dest, progress, 10, 70, ct), ct).ConfigureAwait(false);

        ctx.InstallDir = dest;
        var exePath = ResolveExePath(ctx);

        Report(progress, 75, "更新卸载信息…");
        RepairUninstallRegistry(ctx, exePath);

        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            Report(progress, 85, "更新快捷方式…");
            RepairShortcuts(ctx.ProductName, exePath, dest);
            RepairPinnedTaskbarShortcuts(exePath, dest, ctx.ProductName);
        }

        Report(progress, 92, "清理旧安装目录…");
        await Task.Run(() => TryRemoveOldInstall(source), ct).ConfigureAwait(false);

        Report(progress, 100, "迁移完成");
    }

    private static async Task StopProductProcessesAsync(UninstallContext ctx, CancellationToken ct)
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
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
            finally { p.Dispose(); }
        }
    }

    private static void CopyDirectory(
        string source,
        string dest,
        IProgress<ProgressReport>? progress,
        double fromPercent,
        double toPercent,
        CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var total = Math.Max(1, files.Count);
        var lastPct = fromPercent - 1;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);

            var pct = fromPercent + (i + 1) / (double)total * (toPercent - fromPercent);
            if (pct - lastPct >= 0.5 || i + 1 == total)
            {
                lastPct = pct;
                Report(progress, pct, $"复制文件… {i + 1}/{total}");
            }
        }
    }

    private static void TryRemoveOldInstall(string oldDir)
    {
        var self = Environment.ProcessPath;
        var selfUnderOld = false;
        if (!string.IsNullOrWhiteSpace(self))
        {
            var selfDir = Path.GetFullPath(Path.GetDirectoryName(self)!).TrimEnd('\\', '/');
            var old = Path.GetFullPath(oldDir).TrimEnd('\\', '/');
            selfUnderOld = string.Equals(selfDir, old, StringComparison.OrdinalIgnoreCase)
                || selfDir.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        if (selfUnderOld)
        {
            ScheduleDeleteDirectory(oldDir);
            return;
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(oldDir, recursive: true);
        }
        catch
        {
            ScheduleDeleteDirectory(oldDir);
        }
    }

    private static void ScheduleDeleteDirectory(string dir)
    {
        var cmd = $"ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C " + cmd,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { /* ignore */ }
    }

    private static string? ResolveExePath(UninstallContext ctx)
    {
        var preferred = Path.Combine(ctx.InstallDir, ctx.ExeName);
        if (File.Exists(preferred)) return preferred;
        try
        {
            return Directory.EnumerateFiles(ctx.InstallDir, "*.exe", SearchOption.TopDirectoryOnly)
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

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var c = Path.GetFullPath(candidate).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelatedShortcut(string lnkPath, string productName, string installedExe)
    {
        var name = Path.GetFileNameWithoutExtension(lnkPath);
        if (name.Equals(productName, StringComparison.OrdinalIgnoreCase)
            || name.Contains("玄鸟", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FlyScreen", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath as string ?? "";
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);

            if (string.IsNullOrWhiteSpace(target)) return false;
            if (target.Equals(installedExe, StringComparison.OrdinalIgnoreCase)) return true;

            var fileName = Path.GetFileName(target);
            return fileName.Equals("FlyScreen.exe", StringComparison.OrdinalIgnoreCase)
                   || fileName.Equals("FlyScreen.App.Maui.exe", StringComparison.OrdinalIgnoreCase)
                   || fileName.Equals("FlyScreen.App.Windows.exe", StringComparison.OrdinalIgnoreCase)
                   || target.Contains(@"\FlyScreen\", StringComparison.OrdinalIgnoreCase)
                   || target.Contains(@"\玄鸟\", StringComparison.OrdinalIgnoreCase)
                   || target.Contains(@"\XuanNiao\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateShortcut(string linkPath, string targetPath, string workingDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDir;
        shortcut.Description = description ?? "";
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static void Report(IProgress<ProgressReport>? progress, double percent, string message)
        => progress?.Report(ProgressReport.Create(percent, message));
}
