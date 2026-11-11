using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FlyScreen.Installer.Services;

/// <summary>
/// 可取消、可回滚的安装：
/// 1) 解压到 staging
/// 2) 若已有安装：将原目录改名为 backup（卸载程序文件，不动 %AppData%\FlyScreen 个人数据）
/// 3) staging → installDir
/// 4) 成功删 backup；取消/失败则恢复 backup、清理半成品
/// </summary>
public sealed class InstallService
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FlyScreen";

    public async Task InstallAsync(
        InstallerManifest manifest,
        string installDir,
        string payloadZip,
        bool reinstall,
        IProgress<ProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(payloadZip))
            throw new FileNotFoundException("未找到安装包数据 payload.zip", payloadZip);

        var session = Path.Combine(Path.GetTempPath(), "FlyScreenSetup_" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(session, "staging");
        string? backupDir = null;
        var committed = false;

        Directory.CreateDirectory(staging);

        try
        {
            ct.ThrowIfCancellationRequested();
            // 0–35% 解压
            await Task.Run(() =>
                ExtractZipWithProgress(payloadZip, staging, progress, 0, 35, ct), ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // 35–45% 备份/清理旧目录
            if (reinstall && Directory.Exists(installDir))
            {
                Report(progress, 38, "卸载旧版本（保留个人配置）…");
                await Task.Run(() =>
                {
                    RemoveShortcuts(manifest.ProductName);
                    backupDir = Path.Combine(session, "backup");
                    if (Directory.Exists(backupDir))
                        DeleteDirectoryBestEffort(backupDir);
                    // 安装目录与 %TEMP% 不在同一盘时，Directory.Move 会失败。
                    RelocateDirectory(installDir, backupDir, ct);
                }, ct).ConfigureAwait(false);
                Report(progress, 45, "旧版本已移出…");
            }
            else if (Directory.Exists(installDir))
            {
                Report(progress, 38, "清理目标目录…");
                await Task.Run(() => DeleteDirectoryBestEffort(installDir), ct).ConfigureAwait(false);
                Report(progress, 45, "目标目录已就绪…");
            }
            else
            {
                Report(progress, 45, "准备写入…");
            }

            ct.ThrowIfCancellationRequested();
            // 45–88% 复制文件
            await Task.Run(() =>
            {
                // 安装包只交付 Bundled 源包；剥离可能混入的安装态，安装后由工具页自行安装
                StripToolsInstallState(staging);
                EnsureParentDirectory(installDir);
                CopyDirectory(staging, installDir, progress, 45, 88, ct);
                StripToolsInstallState(installDir);
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var exePath = ResolveExePath(installDir, manifest.ExeName)
                          ?? throw new InvalidOperationException("安装后未找到主程序。");
            manifest.ExeName = Path.GetRelativePath(installDir, exePath);

            var workDir = Path.GetDirectoryName(exePath) ?? installDir;

            Report(progress, 90, "创建快捷方式…");
            if (manifest.CreateStartMenuShortcut)
            {
                var programs = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                var linkDir = Path.Combine(programs, "Programs", manifest.ProductName);
                Directory.CreateDirectory(linkDir);
                ShellShortcut.Create(
                    Path.Combine(linkDir, manifest.ProductName + ".lnk"),
                    exePath, workDir, manifest.ProductName);
            }

            if (manifest.CreateDesktopShortcut)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                ShellShortcut.Create(
                    Path.Combine(desktop, manifest.ProductName + ".lnk"),
                    exePath, workDir, manifest.ProductName);
            }

            RepairPinnedTaskbarShortcuts(exePath, workDir, manifest.ProductName);

            Report(progress, 94, "写入卸载信息…");
            WriteInstallMarker(installDir);
            WriteUninstallInfo(manifest, installDir, exePath);

            if (!string.IsNullOrWhiteSpace(backupDir) && Directory.Exists(backupDir))
            {
                Report(progress, 97, "清理旧版本文件…");
                DeleteDirectoryBestEffort(backupDir);
            }

            committed = true;
            Report(progress, 100, "安装完成");
        }
        catch (OperationCanceledException)
        {
            Report(progress, 99, "正在回滚…");
            Rollback(installDir, backupDir);
            throw;
        }
        catch
        {
            Report(progress, 99, "安装失败，正在回滚…");
            Rollback(installDir, backupDir);
            throw;
        }
        finally
        {
            DeleteDirectoryBestEffort(session);
            if (!committed && !string.IsNullOrWhiteSpace(backupDir) && Directory.Exists(backupDir)
                && !Directory.Exists(installDir))
            {
                // finally 再兜底一次
                try { RelocateDirectory(backupDir, installDir, CancellationToken.None); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 卸载程序文件与快捷方式/注册表，不删除 %AppData%\FlyScreen 等个人配置。
    /// </summary>
    public static void UninstallProgramFiles(string installDir, string productName, IProgress<ProgressReport>? progress = null)
    {
        Report(progress, 10, "移除快捷方式…");
        RemoveShortcuts(productName);

        Report(progress, 30, "移除卸载信息…");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch { /* ignore */ }

        if (Directory.Exists(installDir))
        {
            Report(progress, 50, "移除程序文件…");
            DeleteDirectoryBestEffort(installDir);
        }

        Report(progress, 100, "完成");
    }

    private static void Report(IProgress<ProgressReport>? progress, double percent, string message)
        => progress?.Report(ProgressReport.Create(percent, message));

    /// <summary>
    /// 主程序安装只落地 <c>Tools/Bundled</c>；去掉 Installed / tools-install.json 等运行态，
    /// 避免升级合并或脏 payload 导致工具显示「已安装」却不可用。
    /// </summary>
    private static void StripToolsInstallState(string root)
    {
        if (!Directory.Exists(root)) return;

        foreach (var toolsRoot in Directory.EnumerateDirectories(root, "Tools", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var name in new[]
                         {
                             "tools-install.json",
                             "tools-shortcuts.json",
                             "tools-local-programs.json",
                             "tools-index.cache.json"
                         })
                {
                    var f = Path.Combine(toolsRoot, name);
                    if (File.Exists(f))
                        File.Delete(f);
                }

                foreach (var sub in Directory.EnumerateDirectories(toolsRoot))
                {
                    if (Path.GetFileName(sub).Equals("Bundled", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { Directory.Delete(sub, recursive: true); }
                    catch { /* ignore */ }
                }
            }
            catch
            {
                // 清理失败不阻断安装；运行时仍按未安装展示残缺记录
            }
        }
    }

    private static void ExtractZipWithProgress(
        string zipPath,
        string destDir,
        IProgress<ProgressReport>? progress,
        double fromPercent,
        double toPercent,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var total = Math.Max(1, entries.Count);
        var lastPct = fromPercent - 1;

        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            var target = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            entry.ExtractToFile(target, overwrite: true);

            var pct = fromPercent + (i + 1) / (double)total * (toPercent - fromPercent);
            if (pct - lastPct >= 0.5 || i + 1 == total)
            {
                lastPct = pct;
                Report(progress, pct, $"解压安装包… {i + 1}/{total}");
            }
        }

        Report(progress, toPercent, "解压完成");
    }

    private static void Rollback(string installDir, string? backupDir)
    {
        // 半成品安装目录
        if (Directory.Exists(installDir))
            DeleteDirectoryBestEffort(installDir);

        // 恢复旧版
        if (!string.IsNullOrWhiteSpace(backupDir) && Directory.Exists(backupDir))
        {
            try
            {
                if (!Directory.Exists(installDir))
                    RelocateDirectory(backupDir, installDir, CancellationToken.None);
            }
            catch
            {
                // 保留 backup 供用户手工恢复
            }
        }
    }

    public static void Launch(string installDir, string exeName)
    {
        var exe = ResolveExePath(installDir, exeName);
        if (exe == null || !File.Exists(exe)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? installDir,
            UseShellExecute = true
        });
    }

    private static string? ResolveExePath(string installDir, string exeName)
    {
        var preferred = Path.Combine(installDir, exeName);
        if (File.Exists(preferred)) return preferred;
        try
        {
            return Directory.EnumerateFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    return n.Equals("FlyScreen", StringComparison.OrdinalIgnoreCase)
                           || n.Contains("FlyScreen", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch { return null; }
    }

    /// <summary>同一磁盘用移动；跨盘则复制后再删除源目录。</summary>
    private static void RelocateDirectory(string source, string dest, CancellationToken ct)
    {
        if (SameVolume(source, dest))
        {
            Directory.Move(source, dest);
            return;
        }

        CopyDirectory(source, dest, null, 0, 0, ct);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* ignore */ }
            }

            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException(
                "旧版本与临时目录不在同一磁盘，已复制备份，但无法删除原目录。请关闭正在运行的玄鸟后重试。",
                ex);
        }
    }

    private static bool SameVolume(string a, string b)
    {
        var left = Path.GetPathRoot(Path.GetFullPath(a));
        var right = Path.GetPathRoot(Path.GetFullPath(b));
        return !string.IsNullOrEmpty(left)
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureParentDirectory(string installDir)
    {
        var parent = Path.GetDirectoryName(installDir);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
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
                Report(progress, pct, $"写入程序文件… {i + 1}/{total}");
            }
        }

        Report(progress, toPercent, "程序文件已写入");
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }

            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static void RemoveShortcuts(string productName)
    {
        try
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                productName + ".lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
            var bat = Path.ChangeExtension(desktop, ".bat");
            if (File.Exists(bat)) File.Delete(bat);
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

    public const string UninstallExeName = "Uninstall.exe";

    private static void WriteInstallMarker(string installDir)
    {
        try
        {
            Directory.CreateDirectory(installDir);
            File.WriteAllText(
                Path.Combine(installDir, ".flyscreen-install"),
                "setup\n",
                Encoding.UTF8);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void WriteUninstallInfo(InstallerManifest manifest, string installDir, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        if (key == null) return;

        var uninstallExe = Path.Combine(installDir, UninstallExeName);
        key.SetValue("DisplayName", manifest.ProductName);
        key.SetValue("DisplayVersion", manifest.Version);
        key.SetValue("Publisher", manifest.PublisherName);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, manifest.RequiredBytes / 1024));
        // 启用「修改」：指向卸载器维护模式
        key.SetValue("NoModify", 0);
        key.SetValue("NoRepair", 1);

        if (File.Exists(uninstallExe))
        {
            key.SetValue("UninstallString", $"\"{uninstallExe}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" /S");
            key.SetValue("ModifyPath", $"\"{uninstallExe}\" /modify");
        }
        else
        {
            // 兜底：无卸载器时仍可删目录（不推荐）
            var uninstallCmd = $"cmd.exe /C rmdir /S /Q \"{installDir}\" & reg delete \"HKCU\\{UninstallKeyPath}\" /f";
            key.SetValue("UninstallString", uninstallCmd);
            try { key.DeleteValue("ModifyPath", throwOnMissingValue: false); } catch { /* ignore */ }
            key.SetValue("NoModify", 1);
        }
    }

    private static void RepairPinnedTaskbarShortcuts(string exePath, string workDir, string productName)
    {
        try
        {
            var taskBar = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(taskBar)) return;

            foreach (var lnk in Directory.EnumerateFiles(taskBar, "*.lnk"))
            {
                if (!IsFlyScreenRelatedShortcut(lnk, productName, exePath))
                    continue;
                ShellShortcut.Create(lnk, exePath, workDir, productName);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static bool IsFlyScreenRelatedShortcut(string lnkPath, string productName, string installedExe)
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
}
