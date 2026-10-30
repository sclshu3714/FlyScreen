using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 按需安装 / 卸载 MTT Virtual Display Driver。
/// 安装：离线包优先，其次 winget，再尝试在线下载控制台/驱动包。
/// 仅当本程序成功安装后写入 <see cref="MttVddInstallRecord"/>，卸载清理才开放。
/// </summary>
public static class MttVddInstaller
{
    public const string OfflineFolderName = "VddOffline";

    /// <summary>官方便携控制台（含安装/卸载与管道）；优先于纯驱动包。</summary>
    public const string OnlineControlZipUrl =
        "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VDD.Control.25.7.23.zip";

    /// <summary>
    /// 25.7.23 官方对 Intel/AMD（含 x64 Windows）发布的 Driver.Only 文件名即带 x86，
    /// 并非「仅 32 位系统」；ARM64 机器应改用 <see cref="OnlineDriverZipUrlArm64"/>。
    /// </summary>
    public const string OnlineDriverZipUrl =
        "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";

    public const string OnlineDriverZipUrlArm64 =
        "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-ARM64.Driver.Only.zip";

    public const string WingetId = "VirtualDrivers.Virtual-Display-Driver";

    public static string OfflineDirectory
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, OfflineFolderName);
        }
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

    public static string? FindOfflineDriverZip()
    {
        var dir = OfflineDirectory;
        if (!Directory.Exists(dir)) return null;
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        // Signed-Driver / Driver.Only；排除控制台大包与音频包
        return Directory.EnumerateFiles(dir, "*Driver*.zip", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(dir, "*VDD*.zip", SearchOption.TopDirectoryOnly))
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return n.Contains("Driver", StringComparison.OrdinalIgnoreCase)
                       && !n.Contains("Control", StringComparison.OrdinalIgnoreCase)
                       && !n.Contains("Audio", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(f =>
            {
                var n = Path.GetFileName(f)!;
                if (arm && n.Contains("ARM64", StringComparison.OrdinalIgnoreCase)) return 3;
                if (!arm && (n.Contains("x64", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("x86", StringComparison.OrdinalIgnoreCase))) return 3;
                return 1;
            })
            .FirstOrDefault();
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

        Directory.CreateDirectory(OfflineDirectory);
        var attemptedInstall = false;
        var productAlready = MttVddEnvironment.IsProductInstalled();

        // 0) 软件已装但管道未起：先启设备 + 控制台，避免重复安装仍不可用
        if (productAlready)
        {
            progress?.Report("检测到已安装 Virtual Display Driver，正在启用设备并启动控制台…");
            if (await TryActivateDriverAsync(progress, ct).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "已启动虚拟显示驱动，管道就绪");
            }
        }

        // 1) 离线 setup.exe —— 仅静默参数（Inno：/VERYSILENT；禁止无参弹窗）
        var setup = FindOfflineSetupExecutables().FirstOrDefault();
        if (setup != null)
        {
            attemptedInstall = true;
            progress?.Report("正在静默安装离线包…");
            var code = await RunProcessAsync(setup,
                           "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", ct).ConfigureAwait(false)
                       ?? await RunProcessAsync(setup, "/S /NORESTART", ct).ConfigureAwait(false);
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

        // 3) 离线/在线 Signed-Driver 或 Driver.Only —— pnputil/nefcon 静默装驱动
        var zip = FindOfflineDriverZip();
        if (zip == null)
        {
            progress?.Report(RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "正在下载 ARM64 驱动包…"
                : "正在下载驱动包（官方 x86 命名适用于 x64 Windows）…");
            zip = await TryDownloadDriverZipAsync(ct).ConfigureAwait(false);
        }

        if (zip != null)
        {
            attemptedInstall = true;
            progress?.Report("正在静默安装驱动包…");
            var silent = await TrySilentDriverInstallAsync(zip, FindOfflineNefcon(), ct).ConfigureAwait(false);
            if (silent && await TryActivateDriverAsync(progress, ct).ConfigureAwait(false))
            {
                MttVddInstallRecord.MarkInstalledByApp();
                return (true, "驱动静默安装完成");
            }
        }

        // 4) 仅启动离线控制台拉起管道（非安装 UI；安装应已由上面静默完成）
        var offlineControls = FindOfflineControlExecutables().ToList();
        if (offlineControls.Count > 0)
        {
            progress?.Report("正在启动离线控制台以创建管道…");
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
        else
        {
            // 无离线控制台时再尝试解压/下载 VDD.Control（体积大，非首选）
            progress?.Report("离线控制台缺失，尝试准备 VDD.Control…");
            var controlDir = await TryDownloadAndExtractControlAsync(progress, ct).ConfigureAwait(false);
            if (controlDir != null)
            {
                await TryStartExecutablesUnderAsync(controlDir, ct).ConfigureAwait(false);
                if (await TryActivateDriverAsync(progress, ct).ConfigureAwait(false))
                {
                    MttVddInstallRecord.MarkInstalledByApp();
                    return (true, "Virtual Driver Control 已就绪");
                }
            }
        }

        // 5) 打开离线 README / 下载页指引
        var readme = Path.Combine(OfflineDirectory, "README.txt");
        if (!File.Exists(readme))
            await WriteOfflineReadmeAsync(readme).ConfigureAwait(false);

        if (!attemptedInstall && !productAlready && setup == null)
        {
            return (false,
                "自动静默安装未完成。请将安装包放入程序目录 VddOffline\\ 后重试，或执行：winget install VirtualDrivers.Virtual-Display-Driver --silent");
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
        return (false, pkg
            ? foundControl
                ? "软件包已安装且已尝试启动控制台，但仍无 MTTVirtualDisplayPipe。驱动设备可能需注销或重启后才能加载。请注销/重启后点「重新检测」；或在开始菜单打开「Virtual Display Driver」确认虚拟屏已创建。"
                : "软件包已安装，但未找到控制台程序（管道由其提供）。请从开始菜单打开「Virtual Display Driver」；若没有该程序，请重新安装完整包：winget install VirtualDrivers.Virtual-Display-Driver，然后注销或重启。"
            : "安装步骤已执行，但未检测到驱动。请确认 UAC 已允许安装，并在「设置 → 应用」中查看是否出现 Virtual Display Driver；必要时注销或重启后重试。");
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
        if (!await WaitForDriverReadyAsync(progress, ct, attempts: waitAttempts).ConfigureAwait(false))
            return false;

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

    /// <summary>启用/重启 Root\MttVDD，促使 IDD 与用户态管道起来。</summary>
    private static async Task TryEnableDriverDeviceAsync(CancellationToken ct)
    {
        try
        {
            await RunProcessAsync("pnputil", "/enable-device \"Root\\MttVDD\"", ct).ConfigureAwait(false);
            await RunProcessAsync("pnputil", "/restart-device \"Root\\MttVDD\"", ct).ConfigureAwait(false);
            // 常见硬件 ID / 实例路径变体
            await RunProcessAsync("pnputil", "/enum-devices /class Display", ct).ConfigureAwait(false);
            var nef = FindOfflineNefcon();
            if (nef != null)
            {
                // 若设备不存在，尝试用已有 inf 再装一次（失败忽略）
                var offlineInf = Directory.Exists(OfflineDirectory)
                    ? Directory.EnumerateFiles(OfflineDirectory, "MttVDD.inf", SearchOption.AllDirectories)
                        .FirstOrDefault()
                    : null;
                if (offlineInf != null)
                    await RunProcessAsync(nef, $"install \"{offlineInf}\" Root\\MttVDD", ct)
                        .ConfigureAwait(false);
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
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

            静默优先顺序：
              1) Virtual.Display.Driver-*-setup-x64.exe  (/VERYSILENT)
              2) winget --silent
              3) 按需下载 Driver.Only + pnputil/nefcon
              4) 启动 VDDControl*.exe 拉起管道

            注意：25.7 起官方不再提供 setup；本离线包保留 25.5.2 setup-x64 用于静默安装。

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

        // 清理本程序下载到 VddOffline 的临时驱动 zip（保留 README）
        try
        {
            var downloaded = Path.Combine(OfflineDirectory, "VirtualDisplayDriver-x86.Driver.Only.zip");
            if (File.Exists(downloaded))
            {
                File.Delete(downloaded);
                removed++;
            }
        }
        catch { /* ignore */ }

        return removed;
    }

    private static async Task<string?> TryDownloadAndExtractControlAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        try
        {
            var zipPath = Path.Combine(OfflineDirectory, "VDD.Control.25.7.23.zip");
            var extractDir = Path.Combine(OfflineDirectory, "VDD.Control");
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 1000)
            {
                progress?.Report("正在下载 VDD.Control（约 70MB）…");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                await using var net = await http.GetStreamAsync(OnlineControlZipUrl, ct).ConfigureAwait(false);
                await using var fs = File.Create(zipPath);
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (!Directory.Exists(extractDir)
                || !Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories).Any())
            {
                progress?.Report("正在解压 Virtual Driver Control…");
                if (Directory.Exists(extractDir))
                {
                    try { Directory.Delete(extractDir, recursive: true); }
                    catch { /* ignore */ }
                }

                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            }

            return extractDir;
        }
        catch
        {
            return null;
        }
    }

    private static async Task TryStartExecutablesUnderAsync(string root, CancellationToken ct)
    {
        try
        {
            var exes = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var n = Path.GetFileName(f).ToLowerInvariant();
                    return !n.Contains("uninstall") && !n.Contains("setup") && !n.Contains("nefcon")
                           && (n.Contains("vdd") || n.Contains("control") || n.Contains("virtual")
                               || n.Contains("console") || n == "vdc.exe");
                })
                .Take(6)
                .ToList();

            foreach (var exe in exes)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? root,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    });
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    if (await MttVddEnvironment.IsDriverReadyAsync(ct).ConfigureAwait(false))
                        return;
                }
                catch
                {
                    // try next
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<string?> TryDownloadDriverZipAsync(CancellationToken ct)
    {
        try
        {
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var url = arm ? OnlineDriverZipUrlArm64 : OnlineDriverZipUrl;
            var fileName = arm
                ? "VirtualDisplayDriver-ARM64.Driver.Only.zip"
                : "VirtualDisplayDriver-x86.Driver.Only.zip";
            var dest = Path.Combine(OfflineDirectory, fileName);
            if (File.Exists(dest) && new FileInfo(dest).Length > 1000)
                return dest;

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await using var net = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
            await using var fs = File.Create(dest);
            await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            return dest;
        }
        catch
        {
            return FindOfflineDriverZip();
        }
    }

    private static async Task<bool> TrySilentDriverInstallAsync(
        string zipPath,
        string? nefconPath,
        CancellationToken ct)
    {
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "FlyScreenVdd_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, work, overwriteFiles: true);

            var inf = Directory.EnumerateFiles(work, "MttVDD.inf", SearchOption.AllDirectories).FirstOrDefault()
                      ?? Directory.EnumerateFiles(work, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
            if (inf == null) return false;

            // 优先 nefcon 创建 Root\MttVDD；否则 pnputil 静默装驱动包
            if (!string.IsNullOrWhiteSpace(nefconPath) && File.Exists(nefconPath))
            {
                var code = await RunProcessAsync(nefconPath, $"install \"{inf}\" Root\\MttVDD", ct)
                    .ConfigureAwait(false);
                if (code == 0) return true;
            }

            var pnpu = await RunProcessAsync("pnputil", $"/add-driver \"{inf}\" /install", ct)
                .ConfigureAwait(false);
            return pnpu == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int?> RunProcessAsync(string fileName, string args, CancellationToken ct)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            if (!p.Start()) return null;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode;
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
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                return p.ExitCode;
            }
            catch
            {
                return null;
            }
        }
    }
}
