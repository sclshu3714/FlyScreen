using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FlyScreen.Services;
using Microsoft.Win32;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 按需安装 / 卸载 MTT Virtual Display Driver。
/// 统一使用离线包 v25.05.03（setup + VDDControl），其次 winget；不再下载已放弃的 25.7 Driver.Only。
/// 仅当本程序成功安装后写入 <see cref="MttVddInstallRecord"/>，卸载清理才开放。
/// 发布物：程序目录 VddOffline\FlyScreen.VddOffline.zip（单文件则内置后解压到同相对路径）；
/// 运行时再展开到 %AppData%\FlyScreen\VddOffline。
/// </summary>
public static class MttVddInstaller
{
    public const string OfflineFolderName = "VddOffline";
    public const string OfflineBundleZipName = "FlyScreen.VddOffline.zip";

    public const string WingetId = "VirtualDrivers.Virtual-Display-Driver";

    /// <summary>历史误下的包名（25.7 Driver.Only / Control 大包），启动修复时清理。</summary>
    private static readonly string[] AbandonedOfflineNames =
    [
        "VirtualDisplayDriver-x86.Driver.Only.zip",
        "VirtualDisplayDriver-ARM64.Driver.Only.zip",
        "VDD.Control.25.7.23.zip"
    ];

    private static readonly object OfflineGate = new();

    /// <summary>固化后的离线包目录（%AppData%\FlyScreen\VddOffline）。</summary>
    public static string OfflineDirectory
        => Path.Combine(FlyScreenDataPaths.Root, OfflineFolderName);

    /// <summary>
    /// 从单文件解压目录 / 程序旁的 zip 或文件夹，固化到 <see cref="OfflineDirectory"/>。
    /// </summary>
    public static bool EnsureOfflineMaterialized(IProgress<string>? progress = null)
    {
        lock (OfflineGate)
        {
            try
            {
                var dest = OfflineDirectory;
                Directory.CreateDirectory(dest);
                var stampPath = Path.Combine(dest, "bundle.sha256");

                var zip = FindBundledOfflineZip();
                if (zip != null)
                {
                    string hash;
                    using (var fs = File.OpenRead(zip))
                    using (var sha = SHA256.Create())
                        hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();

                    if (File.Exists(stampPath)
                        && string.Equals(File.ReadAllText(stampPath).Trim(), hash, StringComparison.OrdinalIgnoreCase)
                        && HasUsableOfflinePayload(dest))
                        return true;

                    progress?.Report("正在展开内置虚拟显示离线包…");
                    var tmp = dest + ".extracting";
                    try
                    {
                        if (Directory.Exists(tmp))
                            Directory.Delete(tmp, recursive: true);
                        Directory.CreateDirectory(tmp);
                        ZipFile.ExtractToDirectory(zip, tmp, overwriteFiles: true);
                        // 替换内容：先清旧文件（保留目录）
                        foreach (var f in Directory.EnumerateFiles(dest))
                        {
                            try { File.Delete(f); } catch { /* ignore */ }
                        }

                        foreach (var f in Directory.EnumerateFiles(tmp))
                        {
                            var name = Path.GetFileName(f);
                            File.Copy(f, Path.Combine(dest, name), overwrite: true);
                        }

                        foreach (var d in Directory.EnumerateDirectories(tmp))
                        {
                            var name = Path.GetFileName(d);
                            var target = Path.Combine(dest, name);
                            if (Directory.Exists(target))
                                Directory.Delete(target, recursive: true);
                            CopyDirectory(d, target);
                        }

                        File.WriteAllText(stampPath, hash);
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(tmp))
                                Directory.Delete(tmp, recursive: true);
                        }
                        catch { /* ignore */ }
                    }

                    return HasUsableOfflinePayload(dest);
                }

                // 调试多文件：程序旁已有 VddOffline 文件夹
                var beside = FindBesideOfflineFolder();
                if (beside != null && HasUsableOfflinePayload(beside))
                {
                    if (string.Equals(Path.GetFullPath(beside), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        return true;
                    progress?.Report("正在同步程序目录离线包…");
                    foreach (var f in Directory.EnumerateFiles(beside))
                    {
                        var name = Path.GetFileName(f);
                        if (string.Equals(name, "bundle.sha256", StringComparison.OrdinalIgnoreCase))
                            continue;
                        File.Copy(f, Path.Combine(dest, name), overwrite: true);
                    }

                    return HasUsableOfflinePayload(dest);
                }

                return HasUsableOfflinePayload(dest);
            }
            catch
            {
                return HasUsableOfflinePayload(OfflineDirectory);
            }
        }
    }

    private static bool HasUsableOfflinePayload(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string? FindBundledOfflineZip()
    {
        foreach (var root in EnumerateAppContentRoots())
        {
            // 优先：安装/发布目录 VddOffline\FlyScreen.VddOffline.zip
            var nested = Path.Combine(root, OfflineFolderName, OfflineBundleZipName);
            if (File.Exists(nested))
                return nested;

            // 兼容旧版：根目录旁路 zip
            var legacy = Path.Combine(root, OfflineBundleZipName);
            if (File.Exists(legacy))
                return legacy;
        }

        return null;
    }

    private static string? FindBesideOfflineFolder()
    {
        foreach (var root in EnumerateAppContentRoots())
        {
            var dir = Path.Combine(root, OfflineFolderName);
            try
            {
                // 仅当目录内已有控制台/setup 等 exe（开发机旁路展开）；纯 zip 走 FindBundledOfflineZip
                if (Directory.Exists(dir) && HasUsableOfflinePayload(dir))
                    return dir;
            }
            catch { /* next */ }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAppContentRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath)
                 })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                continue;
            }

            if (seen.Add(full))
                yield return full;
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>离线静默安装包（setup*.exe）；不含控制台 exe。</summary>
    public static IEnumerable<string> FindOfflineSetupExecutables()
    {
        var dir = OfflineDirectory;
        if (!Directory.Exists(dir)) yield break;
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                     .Where(IsSilentSetupCandidate)
                     .OrderByDescending(f => SetupArchScore(Path.GetFileName(f)!, arm)))
            yield return f;
    }

    private static bool IsSilentSetupCandidate(string path)
    {
        var n = Path.GetFileName(path) ?? "";
        if (n.Contains("Control", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return false;
        return n.Contains("setup", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Virtual.Display.Driver", StringComparison.OrdinalIgnoreCase);
    }

    private static int SetupArchScore(string fileName, bool arm)
    {
        if (arm && fileName.Contains("ARM64", StringComparison.OrdinalIgnoreCase)) return 3;
        if (!arm && fileName.Contains("x64", StringComparison.OrdinalIgnoreCase)) return 3;
        if (!arm && fileName.Contains("x86", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    public static string? FindOfflineNefcon()
    {
        var dir = OfflineDirectory;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "nefconw.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>离线控制台（安装后提供管道；非静默安装器）。</summary>
    public static IEnumerable<string> FindOfflineControlExecutables()
    {
        var dir = OfflineDirectory;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var n = Path.GetFileName(f) ?? "";
            if (n.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Contains("Control", StringComparison.OrdinalIgnoreCase)
                || n.Contains("VDDControl", StringComparison.OrdinalIgnoreCase)
                || n.Equals("VDC.exe", StringComparison.OrdinalIgnoreCase))
                yield return f;
        }

        var extracted = Path.Combine(dir, "VDD.Control");
        if (!Directory.Exists(extracted)) yield break;
        foreach (var f in Directory.EnumerateFiles(extracted, "*.exe", SearchOption.AllDirectories))
        {
            var n = Path.GetFileName(f)?.ToLowerInvariant() ?? "";
            if (n.Contains("uninstall") || n.Contains("setup") || n.Contains("nefcon")) continue;
            if (n.Contains("vdd") || n.Contains("control") || n.Contains("virtual") || n == "vdc.exe")
                yield return f;
        }
    }

    /// <summary>执行环境修复；仅当驱动管道真正就绪后才标记为本程序安装。</summary>
    public static async Task<(bool Ok, string Message)> TryRepairAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false)
            || MttVddEnvironment.HasSecondaryMonitor())
            return (true, "环境已就绪，无需安装");

        EnsureOfflineMaterialized(progress);
        Directory.CreateDirectory(OfflineDirectory);
        PurgeAbandonedOfflineDownloads();
        var attemptedInstall = false;
        var productAlready = MttVddEnvironment.IsProductInstalled();
        var hasOfflineControl = FindOfflineControlExecutables().Any();
        var hasInstalledControl = FindVddControlExecutables().Any() || FindVddControlShortcuts().Any();
        var hasOfflineSetup = FindOfflineSetupExecutables().Any();

        // 0) 软件已装但管道未起：自动修黄叹号设备 → 拉控制台；仍失败再静默重建设备（用户无需开设备管理器）。
        if (productAlready)
        {
            // 无任何控制台/离线包时不要长时间重置设备（只会空转并生成空 VddOffline）
            if (!hasOfflineControl && !hasInstalledControl && !hasOfflineSetup)
            {
                return (false,
                    "驱动已安装，但未找到 VDDControl/setup（单文件应内置 FlyScreen.VddOffline.zip）。"
                    + "请更换完整发布的主程序，或手动将控制台放入 %AppData%\\FlyScreen\\VddOffline\\ 后再点「重新检测」。");
            }

            progress?.Report("检测到已安装 Virtual Display Driver，正在启用设备并启动控制台…");
            if (await TryActivateDriverAsync(progress, ct).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "已启动虚拟显示驱动，管道就绪");
            }

            progress?.Report("首次拉起未成功，正在自动修复异常显示设备…");
            await TryHealFaultyVddDeviceAsync(progress, ct).ConfigureAwait(false);
            if (await TryActivateDriverAsync(progress, ct, waitAttempts: 16).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "已修复虚拟显示设备并启动管道");
            }

            progress?.Report("设备仍异常，正在移除并重新创建虚拟显示适配器…");
            await TryRecreateVddDeviceAsync(progress, ct).ConfigureAwait(false);
            if (await TryActivateDriverAsync(progress, ct, waitAttempts: 18).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "已重建虚拟显示设备并启动管道");
            }

            // 设备节点修不好时，用离线 setup / winget 静默重装（仍尽量 /VERYSILENT，不弹设备管理器）
            var setup = FindOfflineSetupExecutables().FirstOrDefault();
            if (setup != null)
            {
                attemptedInstall = true;
                progress?.Report("正在静默重装离线驱动包以恢复设备…");
                await RunProcessAsync(setup,
                        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", ct).ConfigureAwait(false);
                await TryHealFaultyVddDeviceAsync(progress, ct).ConfigureAwait(false);
                if (await TryActivateDriverAsync(progress, ct, waitAttempts: 16).ConfigureAwait(false))
                {
                    MttVddInstallRecord.MarkInstalledByApp();
                    return (true, "静默重装完成，驱动已就绪");
                }
            }

            progress?.Report("正在通过 winget 静默重装…");
            if (await TryWingetInstallAsync(ct).ConfigureAwait(false))
            {
                attemptedInstall = true;
                await TryHealFaultyVddDeviceAsync(progress, ct).ConfigureAwait(false);
                if (await TryActivateDriverAsync(progress, ct, waitAttempts: 16).ConfigureAwait(false))
                {
                    MttVddInstallRecord.MarkInstalledByApp();
                    return (true, "winget 静默重装完成，驱动已就绪");
                }
            }

            return (false,
                "已自动尝试修复/重建虚拟显示设备并重装驱动，但仍无控制台管道。"
                + "请重启电脑后再打开投影点「重新检测」（部分黄叹号设备需重启才能加载）。"
                + DetectCompetingVirtualDisplayHint());
        }

        // 1) 离线 setup.exe —— 仅静默参数（Inno：/VERYSILENT；禁止无参弹窗）
        var setupFresh = FindOfflineSetupExecutables().FirstOrDefault();
        if (setupFresh != null)
        {
            attemptedInstall = true;
            progress?.Report("正在静默安装离线包…");
            var code = await RunProcessAsync(setupFresh,
                           "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", ct).ConfigureAwait(false)
                       ?? await RunProcessAsync(setupFresh, "/S /NORESTART", ct).ConfigureAwait(false);
            progress?.Report(code == 0
                ? "静默安装已完成，正在启用驱动…"
                : "静默安装已请求（请确认 UAC），正在等待驱动…");
            if (await TryActivateDriverAsync(progress, ct).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "离线静默安装完成，驱动已就绪");
            }
        }

        // 2) winget 静默
        progress?.Report(productAlready ? "正在通过 winget 静默修复…" : "正在通过 winget 静默安装…");
        var winget = await TryWingetInstallAsync(ct).ConfigureAwait(false);
        if (winget)
        {
            attemptedInstall = true;
            progress?.Report("winget 静默安装已执行，正在启用驱动…");
            if (await TryActivateDriverAsync(progress, ct).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "winget 静默安装完成，驱动已就绪");
            }
        }

        // 3) 仅启动离线 v25.05.03 控制台拉起管道（不再下载已放弃的 25.7 Driver.Only / Control 大包）
        var offlineControls = FindOfflineControlExecutables().ToList();
        if (offlineControls.Count > 0)
        {
            progress?.Report("正在启动离线控制台（v25.05.03）以创建管道…");
            foreach (var exe in offlineControls.Take(3))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? OfflineDirectory,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                }
                catch { /* next */ }
            }

            if (await TryActivateDriverAsync(progress, ct, waitAttempts: 10).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "控制台已启动，驱动管道就绪");
            }
        }

        // 5) 打开离线 README / 下载页指引
        var readme = Path.Combine(OfflineDirectory, "README.txt");
        if (!File.Exists(readme))
            await WriteOfflineReadmeAsync(readme).ConfigureAwait(false);

        if (!attemptedInstall && !productAlready && setupFresh == null)
        {
            return (false,
                "自动静默安装未完成。请确认主程序内置离线包，或将 setup/VDDControl 放入 %AppData%\\FlyScreen\\VddOffline\\ 后重试，或执行：winget install VirtualDrivers.Virtual-Display-Driver --silent");
        }

        // 最后再激活一轮；仍失败则不写安装标记（卸载卡片须等环境真正通过）
        if (await TryActivateDriverAsync(progress, ct, waitAttempts: 8).ConfigureAwait(false))
        {
            MttVddInstallRecord.MarkInstalledByApp();
            return (true, "驱动已就绪");
        }

        // 清除此前误标（旧逻辑曾在未就绪时写入）
        MttVddInstallRecord.Clear();

        var pkg = MttVddEnvironment.IsProductInstalled();
        var foundControl = FindVddControlExecutables().Any();
        var compete = DetectCompetingVirtualDisplayHint();
        return (false, pkg
            ? foundControl
                ? "已自动修复/重建虚拟显示设备并尝试启动控制台，但仍无管道。请重启电脑后再打开投影点「重新检测」。" + compete
                : "软件包已安装，但未找到控制台程序。请确认 %AppData%\\FlyScreen\\VddOffline\\ 含 VDDControl，或更换内置离线包的完整主程序后再试；仍失败请重启电脑。" + compete
            : "安装步骤已执行，但未检测到驱动。请确认已允许安装；必要时重启后再试，或执行 winget install VirtualDrivers.Virtual-Display-Driver --silent。" + compete);
    }

    /// <summary>启用设备 → 启动控制台 → 轮询管道 → 创建虚拟屏并切到扩展。</summary>
    private static async Task<bool> TryActivateDriverAsync(
        IProgress<string>? progress,
        CancellationToken ct,
        int waitAttempts = 14)
    {
        progress?.Report("正在启用虚拟显示设备…");
        await TryEnableDriverDeviceAsync(ct).ConfigureAwait(false);
        progress?.Report("正在启动 Virtual Display Driver 控制台…");
        await TryStartVddControlAsync(ct).ConfigureAwait(false);
        if (!await WaitForDriverReadyAsync(progress, ct, attempts: Math.Max(4, waitAttempts / 2), delayMs: 1200)
                .ConfigureAwait(false))
        {
            // 黄叹号等异常：先自动修设备再拉一次控制台
            await TryHealFaultyVddDeviceAsync(progress, ct).ConfigureAwait(false);
            await TryStartVddControlAsync(ct).ConfigureAwait(false);
            if (!await WaitForDriverReadyAsync(progress, ct, attempts: waitAttempts, delayMs: 1500)
                    .ConfigureAwait(false))
                return false;
        }

        // 管道就绪仍可能 count=0 / 复制模式，导致「设置→显示」看不到独立副屏
        if (await MttVddEnvironment.EnsureVirtualMonitorVisibleAsync(progress, ct).ConfigureAwait(false))
            return true;
        return MttVddEnvironment.HasSecondaryMonitor();
    }

    /// <summary>安装后轮询管道就绪。</summary>
    private static async Task<bool> WaitForDriverReadyAsync(
        IProgress<string>? progress,
        CancellationToken ct,
        int attempts = 12,
        int delayMs = 2500)
    {
        for (var i = 1; i <= attempts; i++)
        {
            if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
                return true;
            progress?.Report($"等待虚拟显示驱动就绪… ({i}/{attempts})");
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        return await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>离线包或已装目录里是否找得到控制台 exe（可用来拉管道）。</summary>
    public static bool HasAnyControlExecutable()
    {
        try
        {
            return FindOfflineControlExecutables().Any()
                   || HasInstalledControlExecutable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>系统已安装目录/快捷方式中的控制台（不含仅 AppData 离线包）。</summary>
    public static bool HasInstalledControlExecutable()
    {
        try
        {
            return FindVddControlExecutables().Any() || FindVddControlShortcuts().Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本机是否存在向日葵/ToDesk 等其它虚拟显示驱动（可能抢占扩展屏）。</summary>
    public static string DetectCompetingVirtualDisplayHint()
    {
        try
        {
            var names = new List<string>();
            string[] markers =
            [
                "OrayIddDriver", "OrayIdd", "ToDesk", "Moonlight", "ParsecVDA",
                "Sunshine", "UsbMMIdd"
            ];
            using var display = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display != null)
            {
                foreach (var subName in display.GetSubKeyNames())
                {
                    if (subName is "Properties" or "Configuration") continue;
                    using var sub = display.OpenSubKey(subName);
                    var desc = (sub?.GetValue("DriverDesc") as string) ?? "";
                    foreach (var m in markers)
                    {
                        if (desc.Contains(m, StringComparison.OrdinalIgnoreCase)
                            && !names.Contains(desc, StringComparer.OrdinalIgnoreCase))
                            names.Add(desc);
                    }
                }
            }

            if (names.Count == 0) return "";
            return " 检测到其它虚拟显示驱动（" + string.Join("、", names.Take(3))
                   + "），可能与 MTT VDD 冲突，建议先在设备管理器中禁用后再修复。";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 软件已装但管道未监听时拉起控制台。不修改显示器数量，也不调用 DisplaySwitch。
    /// SETDISPLAYCOUNT / 停投后驱动重载常把 VDDControl 打掉，必须轮询等待管道回来，
    /// 否则下次打开投影会误判成「需要自动修复」并走重装。
    /// </summary>
    public static async Task<bool> TryEnsureConsoleAsync(
        CancellationToken ct = default,
        int waitAttempts = 10,
        int delayMs = 700)
    {
        if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
            return true;

        EnsureOfflineMaterialized();
        if (!MttVddEnvironment.IsProductInstalled() && !HasAnyControlExecutable())
            return false;

        PurgeAbandonedOfflineDownloads();
        await TryStartVddControlAsync(ct).ConfigureAwait(false);
        if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
            return true;

        // 驱动重载后控制台可能晚几秒才绑上管道
        return await WaitForDriverReadyAsync(null, ct, attempts: waitAttempts, delayMs: delayMs)
            .ConfigureAwait(false);
    }

    /// <summary>尽力启动 VDD 控制台，使命名管道开始监听。</summary>
    private static async Task TryStartVddControlAsync(CancellationToken ct)
    {
        try
        {
            // 已在跑则不必重复拉起
            if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
                return;

            var candidates = FindOfflineControlExecutables()
                .Concat(FindVddControlExecutables())
                .Concat(FindVddControlShortcuts())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    });
                    await Task.Delay(2500, ct).ConfigureAwait(false);
                    if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
                        return;
                }
                catch
                {
                    // try next
                }
            }

            // 提升权限再试一次（部分构建仅管理员会话建管道）
            foreach (var path in candidates.Take(4))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Normal
                    });
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
                        return;
                }
                catch
                {
                    // UAC 取消等
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>启用/重启已存在的 MttVDD 设备节点（不存在则跳过，避免对幽灵 ID 空转）。</summary>
    private static async Task TryEnableDriverDeviceAsync(CancellationToken ct)
    {
        try
        {
            var ids = await EnumerateVddDeviceInstanceIdsAsync(ct).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                // 仅当产品已装时尝试一次通用 Root 路径；否则不要盲启不存在的节点
                if (MttVddEnvironment.IsProductInstalled())
                {
                    await RunProcessAsync("pnputil", "/enable-device \"Root\\MttVDD\"", ct, timeoutMs: 20_000)
                        .ConfigureAwait(false);
                    await RunProcessAsync("pnputil", "/restart-device \"Root\\MttVDD\"", ct, timeoutMs: 20_000)
                        .ConfigureAwait(false);
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
                return;
            }

            foreach (var id in ids)
            {
                await RunProcessAsync("pnputil", $"/enable-device \"{id}\"", ct, timeoutMs: 20_000)
                    .ConfigureAwait(false);
                await RunProcessAsync("pnputil", $"/restart-device \"{id}\"", ct, timeoutMs: 20_000)
                    .ConfigureAwait(false);
            }

            var nef = FindOfflineNefcon();
            if (nef != null)
            {
                var offlineInf = Directory.Exists(OfflineDirectory)
                    ? Directory.EnumerateFiles(OfflineDirectory, "MttVDD.inf", SearchOption.AllDirectories)
                        .FirstOrDefault()
                    : null;
                if (offlineInf != null)
                    await RunProcessAsync(nef, $"install \"{offlineInf}\" Root\\MttVDD", ct, timeoutMs: 90_000)
                        .ConfigureAwait(false);
            }

            await Task.Delay(800, ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 对应设备管理器里对黄叹号设备的：禁用→启用→重启，以及扫描硬件。
    /// 仅操作真实枚举到的 MTT 节点；禁止对不存在的 ROOT\MTTVDD\0000 盲重置（会卡住）。
    /// </summary>
    private static async Task TryHealFaultyVddDeviceAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report("正在自动修复异常的虚拟显示设备…");
            TryKillVddControlProcesses();

            var ids = await EnumerateVddDeviceInstanceIdsAsync(ct).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                progress?.Report("未找到 MTT 虚拟显示设备节点，跳过重置，改为扫描硬件…");
                await RunProcessAsync("pnputil", "/scan-devices", ct, timeoutMs: 30_000).ConfigureAwait(false);
                await Task.Delay(800, ct).ConfigureAwait(false);
                return;
            }

            foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"正在重置设备 {ShortInstance(id)}…");
                await RunProcessAsync("pnputil", $"/disable-device \"{id}\"", ct, timeoutMs: 20_000)
                    .ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
                await RunProcessAsync("pnputil", $"/enable-device \"{id}\"", ct, timeoutMs: 20_000)
                    .ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
                await RunProcessAsync("pnputil", $"/restart-device \"{id}\"", ct, timeoutMs: 20_000)
                    .ConfigureAwait(false);
            }

            // PowerShell 再兜一层（仅 MttVDD / Virtual Display Driver，勿匹配 Oray 等）
            await RunPowerShellAsync(
                    """
                    Get-PnpDevice -ErrorAction SilentlyContinue |
                      Where-Object {
                        $_.InstanceId -match 'MttVDD|MTTVDD' -or
                        ($_.FriendlyName -match 'Virtual Display Driver|MttVDD' -and $_.FriendlyName -notmatch 'Oray|ToDesk|Parsec')
                      } |
                      ForEach-Object {
                        try { Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue } catch {}
                        Start-Sleep -Milliseconds 400
                        try { Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue } catch {}
                      }
                    """,
                    ct)
                .ConfigureAwait(false);

            await RunProcessAsync("pnputil", "/scan-devices", ct, timeoutMs: 30_000).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>移除异常设备节点并重新创建（仍不弹设备管理器）。</summary>
    private static async Task TryRecreateVddDeviceAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report("正在移除异常虚拟显示设备…");
            TryKillVddControlProcesses();

            var nef = FindOfflineNefcon();
            if (nef != null)
                await RunProcessAsync(nef, "remove Root\\MttVDD", ct).ConfigureAwait(false);

            foreach (var id in await EnumerateVddDeviceInstanceIdsAsync(ct).ConfigureAwait(false))
                await RunProcessAsync("pnputil", $"/remove-device \"{id}\" /force", ct).ConfigureAwait(false);

            await RunProcessAsync("pnputil", "/remove-device \"Root\\MttVDD\" /force", ct)
                .ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);

            progress?.Report("正在重新创建虚拟显示设备…");
            var offlineInf = Directory.Exists(OfflineDirectory)
                ? Directory.EnumerateFiles(OfflineDirectory, "MttVDD.inf", SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;
            if (nef != null && offlineInf != null)
                await RunProcessAsync(nef, $"install \"{offlineInf}\" Root\\MttVDD", ct).ConfigureAwait(false);
            else
                await RunProcessAsync("pnputil", "/scan-devices", ct).ConfigureAwait(false);

            await Task.Delay(1500, ct).ConfigureAwait(false);
            await TryHealFaultyVddDeviceAsync(progress, ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryKillVddControlProcesses()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    if (n.Contains("VDDControl", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("VDD.Control", StringComparison.OrdinalIgnoreCase)
                        || (n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && n.Contains("Display", StringComparison.OrdinalIgnoreCase)
                            && n.Contains("Control", StringComparison.OrdinalIgnoreCase)))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
    }

    private static async Task<List<string>> EnumerateVddDeviceInstanceIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ps = await RunProcessCaptureAsync(
                    "powershell",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { ($_.InstanceId -match 'MttVDD|MTTVDD') -or ($_.FriendlyName -match 'Virtual Display Driver|MttVDD' -and $_.FriendlyName -notmatch 'Oray|ToDesk|Parsec') } | Select-Object -ExpandProperty InstanceId\"",
                    ct)
                .ConfigureAwait(false);
            foreach (var line in (ps ?? "").Split('\r', '\n'))
            {
                var id = line.Trim();
                if (id.Length > 3 && id.Contains('\\'))
                    ids.Add(id);
            }
        }
        catch { /* ignore */ }

        try
        {
            var enumOut = await RunProcessCaptureAsync("pnputil", "/enum-devices /class Display", ct)
                .ConfigureAwait(false);
            foreach (var id in ParsePnputilInstanceIds(enumOut, vddOnly: true))
                ids.Add(id);

            var problemOut = await RunProcessCaptureAsync("pnputil", "/enum-devices /problem", ct)
                .ConfigureAwait(false);
            foreach (var id in ParsePnputilInstanceIds(problemOut, vddOnly: true))
                ids.Add(id);
        }
        catch { /* ignore */ }

        return ids.ToList();
    }

    private static IEnumerable<string> ParsePnputilInstanceIds(string? text, bool vddOnly)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        string? pendingId = null;
        var related = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Instance ID:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("实例 ID:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("实例ID:", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingId != null && (!vddOnly || related))
                    yield return pendingId;
                pendingId = line.Split(':', 2) is { Length: 2 } parts ? parts[1].Trim() : null;
                related = pendingId != null && LooksLikeVddInstance(pendingId);
                continue;
            }

            if (pendingId == null) continue;
            if (line.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
                || line.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
                || line.Contains("MTT VDD", StringComparison.OrdinalIgnoreCase))
                related = true;

            if (string.IsNullOrEmpty(line))
            {
                if (!vddOnly || related)
                    yield return pendingId;
                pendingId = null;
                related = false;
            }
        }

        if (pendingId != null && (!vddOnly || related))
            yield return pendingId;
    }

    private static bool LooksLikeVddInstance(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        // 勿把 Oray/ToDesk 等其它 IDD 当成 MTT
        if (instanceId.Contains("Oray", StringComparison.OrdinalIgnoreCase)
            || instanceId.Contains("ToDesk", StringComparison.OrdinalIgnoreCase)
            || instanceId.Contains("Parsec", StringComparison.OrdinalIgnoreCase))
            return false;
        return instanceId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
               || instanceId.Contains("MTTVDD", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortInstance(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        return id.Length <= 42 ? id : "…" + id[^36..];
    }

    private static async Task RunPowerShellAsync(string script, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        await RunProcessAsync("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}", ct)
            .ConfigureAwait(false);
    }

    private static async Task<string?> RunProcessCaptureAsync(
        string fileName,
        string args,
        CancellationToken ct,
        int timeoutMs = 45_000)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            if (!p.Start()) return null;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                var stdout = await p.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                try { await p.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false); }
                catch { /* ignore */ }
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return stdout;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FindVddControlExecutables()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in EnumerateVddInstallDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var n = name.ToLowerInvariant();
                if (n.Contains("uninstall") || n.Contains("setup") || n.Contains("nefcon")
                    || n.Contains("installer") || n.Contains("update"))
                    continue;
                // 控制台/主程序常见命名；目录内其余可疑 exe 亦纳入（靠启动后 Ping 过滤）
                var likely = n.Contains("vdd") || n.Contains("virtual") || n.Contains("mtt")
                             || n.Contains("control") || n.Contains("console");
                if (!likely) continue;
                if (seen.Add(f))
                    yield return f;
            }
        }
    }

    private static IEnumerable<string> FindVddControlShortcuts()
    {
        var roots = new List<string>();
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.StartMenu,
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.CommonDesktopDirectory
                 })
        {
            try
            {
                var root = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(root))
                    roots.Add(root);
            }
            catch { /* ignore */ }
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var lnk in links)
            {
                var name = Path.GetFileNameWithoutExtension(lnk) ?? "";
                if (LooksLikeVddProduct(name) || name.Contains("VDD", StringComparison.OrdinalIgnoreCase))
                    yield return lnk;
            }
        }
    }

    private static IEnumerable<string> EnumerateVddInstallDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = path.Trim().Trim('"');
                if (File.Exists(path))
                    path = Path.GetDirectoryName(path) ?? path;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    seen.Add(path);
            }
            catch { /* ignore */ }
        }

        // 注册表安装位置 / 图标路径
        try
        {
            string[] roots =
            [
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            ];
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var root in roots)
                {
                    using var key = hive.OpenSubKey(root);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub == null) continue;
                        var display = (sub.GetValue("DisplayName") as string) ?? "";
                        if (!LooksLikeVddProduct(display)) continue;
                        Add(sub.GetValue("InstallLocation") as string);
                        var icon = sub.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            var comma = icon.IndexOf(',');
                            Add(comma > 0 ? icon[..comma] : icon);
                        }

                        var uninstall = (sub.GetValue("UninstallString") as string) ?? "";
                        if (!string.IsNullOrWhiteSpace(uninstall))
                        {
                            var (file, _) = SplitCommand(uninstall);
                            Add(file);
                        }
                    }
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
            Add(key?.GetValue("VDDPATH") as string);
        }
        catch { /* ignore */ }

        Add(@"C:\VirtualDisplayDriver");

        // 常见固定目录 + WinGet 包目录
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.CommonApplicationData
                 })
        {
            string root;
            try { root = Environment.GetFolderPath(folder); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var dirName in new[]
                     {
                         "Virtual Display Driver", "VirtualDisplayDriver", "MttVDD", "VDD.Control",
                         "VDD", "VirtualDrivers"
                     })
                Add(Path.Combine(root, dirName));

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (LooksLikeVddProduct(Path.GetFileName(dir)))
                        Add(dir);
                }
            }
            catch { /* ignore */ }
        }

        try
        {
            var winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
            {
                foreach (var dir in Directory.EnumerateDirectories(winget))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("VDD", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Mtt", StringComparison.OrdinalIgnoreCase))
                        Add(dir);
                }
            }
        }
        catch { /* ignore */ }

        return seen;
    }

    /// <summary>
    /// 卸载并清理由本程序安装的 MTT/VDD 相关程序与数据。
    /// 仅应在 <see cref="MttVddInstallRecord.IsInstalledByApp"/> 为 true 时调用。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TryUninstallAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!MttVddInstallRecord.IsInstalledByApp)
            return (false, "当前驱动非本程序安装，未提供卸载");

        var steps = 0;

        progress?.Report("正在通过 winget 卸载…");
        if (await TryWingetUninstallAsync(ct).ConfigureAwait(false))
            steps++;

        progress?.Report("正在执行注册表卸载项…");
        steps += await TryRegistryUninstallAsync(ct).ConfigureAwait(false);

        progress?.Report("正在移除驱动设备…");
        if (await TryRemoveDriverDeviceAsync(ct).ConfigureAwait(false))
            steps++;

        progress?.Report("正在清理残留文件与目录…");
        steps += CleanupLeftoverFolders();

        await Task.Delay(1500, ct).ConfigureAwait(false);
        var stillReady = await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false);

        MttVddInstallRecord.Clear();

        if (stillReady)
        {
            return (false,
                steps > 0
                    ? "已执行卸载步骤，但仍检测到驱动管道。请注销或重启后再确认，必要时用「应用和功能」手动卸载 Virtual Display Driver。"
                    : "未能自动卸载。请在「应用和功能」中手动卸载 Virtual Display Driver。");
        }

        return (true, steps > 0
            ? "已卸载并清理本程序安装的虚拟显示驱动"
            : "已清除本程序安装记录；系统中未再检测到驱动");
    }

    private static async Task WriteOfflineReadmeAsync(string path)
    {
        var text =
            """
            FlyScreen 虚拟显示驱动离线包（静默安装）
            ========================================
            完整清单与说明见仓库 docs\mtt_vdd\README.md。
            开发机请运行 docs\mtt_vdd\fetch-offline-pack.ps1，写入 tools\VddOffline\。

            统一版本：v25.05.03（对应官方 25.5.2 标签）
            静默优先顺序：
              1) Virtual.Display.Driver-v25.05.03-setup-x64.exe  (/VERYSILENT)
              2) winget --silent
              3) 启动 VDDControl-v25.05.03.exe 拉起管道

            不再下载 VirtualDisplayDriver-*-Driver.Only.zip（已放弃）。

            """;
        await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
    }

    private static async Task<bool> TryWingetInstallAsync(CancellationToken ct)
    {
        try
        {
            var code = await RunProcessAsync("winget",
                $"install --id={WingetId} -e --silent --disable-interactivity --accept-package-agreements --accept-source-agreements",
                ct).ConfigureAwait(false);
            return code is 0 or -1978335189; // 已安装等
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryWingetUninstallAsync(CancellationToken ct)
    {
        try
        {
            var code = await RunProcessAsync("winget",
                $"uninstall --id={WingetId} -e --disable-interactivity --accept-source-agreements",
                ct).ConfigureAwait(false);
            // 0 成功；部分版本「未找到包」仍算可继续
            return code is null or 0 or -1978335212;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> TryRegistryUninstallAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var cmd in EnumerateUninstallCommands())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (file, args) = SplitCommand(cmd);
                if (string.IsNullOrWhiteSpace(file)) continue;
                var code = await RunProcessAsync(file, args, ct).ConfigureAwait(false);
                if (code is 0 or null) count++;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch
            {
                // continue
            }
        }

        return count;
    }

    private static IEnumerable<string> EnumerateUninstallCommands()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in roots)
            {
                RegistryKey? key;
                try { key = hive.OpenSubKey(root); }
                catch { continue; }
                if (key == null) continue;
                using (key)
                {
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub == null) continue;
                        var display = (sub.GetValue("DisplayName") as string) ?? "";
                        if (!LooksLikeVddProduct(display)) continue;
                        var uninstall = (sub.GetValue("QuietUninstallString") as string)
                                        ?? (sub.GetValue("UninstallString") as string);
                        if (!string.IsNullOrWhiteSpace(uninstall))
                            yield return uninstall.Trim();
                    }
                }
            }
        }
    }

    private static bool LooksLikeVddProduct(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var n = displayName.ToLowerInvariant();
        return n.Contains("virtual display", StringComparison.Ordinal)
               || n.Contains("virtual-display", StringComparison.Ordinal)
               || n.Contains("mttvdd", StringComparison.Ordinal)
               || n.Contains("mtt vdd", StringComparison.Ordinal)
               || (n.Contains("vdd") && n.Contains("virtual"));
    }

    private static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
            {
                var file = command[1..end];
                var args = end + 1 < command.Length ? command[(end + 1)..].Trim() : "";
                // 常见 MSI：msiexec /x {guid} → 加 /qn
                if (file.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
                    && !args.Contains("/qn", StringComparison.OrdinalIgnoreCase)
                    && !args.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                    args = args + " /qn";
                else if (!args.Contains("/S", StringComparison.OrdinalIgnoreCase)
                         && !args.Contains("/silent", StringComparison.OrdinalIgnoreCase)
                         && !args.Contains("/quiet", StringComparison.OrdinalIgnoreCase)
                         && !file.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                    args = string.IsNullOrEmpty(args) ? "/S" : args + " /S";
                return (file, args);
            }
        }

        var sp = command.IndexOf(' ');
        if (sp < 0) return (command, "/S");
        var f = command[..sp];
        var a = command[(sp + 1)..];
        if (f.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
            && !a.Contains("/qn", StringComparison.OrdinalIgnoreCase))
            a += " /qn";
        return (f, a);
    }

    private static async Task<bool> TryRemoveDriverDeviceAsync(CancellationToken ct)
    {
        try
        {
            var nef = FindOfflineNefcon();
            if (nef != null)
            {
                var code = await RunProcessAsync(nef, "remove Root\\MttVDD", ct).ConfigureAwait(false);
                if (code == 0) return true;
            }

            // pnputil 删除驱动包（尽力）
            await RunProcessAsync("pnputil", "/remove-device \"Root\\MttVDD\" /force", ct)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int CleanupLeftoverFolders()
    {
        var removed = 0;
        var candidates = new List<string>();

        void AddUnder(Environment.SpecialFolder folder, params string[] names)
        {
            try
            {
                var root = Environment.GetFolderPath(folder);
                if (string.IsNullOrWhiteSpace(root)) return;
                foreach (var n in names)
                    candidates.Add(Path.Combine(root, n));
            }
            catch { /* ignore */ }
        }

        AddUnder(Environment.SpecialFolder.ProgramFiles,
            "Virtual Display Driver", "VirtualDisplayDriver", "MttVDD", "VDD.Control");
        AddUnder(Environment.SpecialFolder.ProgramFilesX86,
            "Virtual Display Driver", "VirtualDisplayDriver", "MttVDD", "VDD.Control");
        AddUnder(Environment.SpecialFolder.LocalApplicationData,
            "Virtual Display Driver", "VirtualDisplayDriver", "MttVDD", "VDD.Control");
        AddUnder(Environment.SpecialFolder.ApplicationData,
            "Virtual Display Driver", "VirtualDisplayDriver", "MttVDD", "VDD.Control");
        AddUnder(Environment.SpecialFolder.CommonApplicationData,
            "Virtual Display Driver", "VirtualDisplayDriver", "MttVDD");

        // Program Files 下模糊匹配
        try
        {
            foreach (var pf in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (string.IsNullOrWhiteSpace(pf) || !Directory.Exists(pf)) continue;
                foreach (var dir in Directory.EnumerateDirectories(pf))
                {
                    var name = Path.GetFileName(dir);
                    if (LooksLikeVddProduct(name))
                        candidates.Add(dir);
                }
            }
        }
        catch { /* ignore */ }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                }
            }
            catch
            {
                // 可能被占用，忽略
            }
        }

        removed += PurgeAbandonedOfflineDownloads();
        return removed;
    }

    /// <summary>删除历史误下的 25.7 Driver.Only / Control 大包，避免占用磁盘或被误用。</summary>
    public static int PurgeAbandonedOfflineDownloads()
    {
        var removed = 0;
        try
        {
            var dir = OfflineDirectory;
            if (!Directory.Exists(dir)) return 0;
            foreach (var name in AbandonedOfflineNames)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch { /* 占用则跳过 */ }
            }

            // 通配：任何 *Driver.Only*.zip
            foreach (var f in Directory.EnumerateFiles(dir, "*Driver.Only*.zip", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(f);
                    removed++;
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        return removed;
    }

    /// <summary>
    /// 运行子进程。主程序已是 requireAdministrator，默认不再 runas（避免 UAC/幽灵设备上永久挂起）。
    /// </summary>
    private static async Task<int?> RunProcessAsync(
        string fileName,
        string args,
        CancellationToken ct,
        int timeoutMs = 120_000,
        bool elevate = false)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = elevate,
                    Verb = elevate ? "runas" : "",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = !elevate
                }
            };
            if (!elevate)
            {
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
            }

            if (!p.Start()) return null;
            return await WaitExitWithTimeoutAsync(p, ct, timeoutMs).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                if (!p.Start()) return null;
                return await WaitExitWithTimeoutAsync(p, ct, timeoutMs).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }

    private static async Task<int?> WaitExitWithTimeoutAsync(Process p, CancellationToken ct, int timeoutMs)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            return null;
        }
    }
}
