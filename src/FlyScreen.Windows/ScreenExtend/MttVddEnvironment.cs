using System.Xml.Linq;
using Microsoft.Win32;

namespace FlyScreen.ScreenExtend;

/// <summary>扩展投影所需第二输出：真副屏或 MTT 虚拟显示驱动。</summary>
public static class MttVddEnvironment
{
    public static bool HasSecondaryMonitor()
        => DisplayMonitorEnum.FindBestExtendTarget() != null;

    public static async Task<bool> IsDriverReadyAsync(CancellationToken ct = default)
    {
        // 新版 Control 常可连接 MTTVirtualDisplayPipe，但 PING 无 PONG；管道可连即视为控制台已运行
        if (await MttVddPipeClient.IsPipePresentAsync(ct).ConfigureAwait(false))
            return true;
        return await MttVddPipeClient.PingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保至少 1 块虚拟屏出现在「设置 → 系统 → 显示」的扩展拓扑中，
    /// 并把分辨率对齐主屏、关闭硬件光标（避免扩展屏看不见指针）。
    /// </summary>
    public static async Task<bool> EnsureVirtualMonitorVisibleAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        Services.ScreenExtend.LayoutEdgeSide layoutSide = Services.ScreenExtend.LayoutEdgeSide.Unknown,
        int? secondaryWidth = null,
        int? secondaryHeight = null,
        int? secondaryRefreshHz = null)
    {
        if (!await IsDriverReadyAsync(ct).ConfigureAwait(false)
            && !HasSecondaryMonitor())
            return false;

        // 改拓扑前快照主屏，停止投影时恢复（禁止 /internal）
        DisplayTopologyGuard.CaptureIfEmpty();

        var primary = DisplayModeHelper.GetPrimaryInfo();
        var tw = secondaryWidth is > 0 ? secondaryWidth.Value : primary.Width;
        var th = secondaryHeight is > 0 ? secondaryHeight.Value : primary.Height;
        var tr = secondaryRefreshHz is > 0 ? secondaryRefreshHz.Value : primary.RefreshHz;
        progress?.Report($"正在配置虚拟屏 {tw}x{th}（主屏 {primary.Width}x{primary.Height} 不变）…");
        TryAlignVddSettings(tw, th, tr);

        if (!HasSecondaryMonitor())
        {
            progress?.Report("正在创建虚拟显示器…");
            try
            {
                await MttVddPipeClient.SetDisplayCountAsync(1, ct).ConfigureAwait(false);
            }
            catch
            {
                // 部分构建无响应；仍继续尝试扩展拓扑
            }

            // SETDISPLAYCOUNT 会重载驱动并弄掉控制台；先把管道拉回，再切扩展，否则会出现
            // 「检查通过 → 点开始投影立刻失败」。
            progress?.Report("正在恢复虚拟显示控制台…");
            await Task.Delay(400, ct).ConfigureAwait(false);
            await MttVddInstaller.TryEnsureConsoleAsync(ct, waitAttempts: 12, delayMs: 600)
                .ConfigureAwait(false);

            // DisplaySwitch 至多一次：反复 /extend 会拖垮 DWM/显示栈直至整机卡死
            progress?.Report("正在切换为扩展显示…");
            DisplayProjection.TryApply(Services.ScreenExtend.DisplayProjectionMode.Extend);

            for (var i = 0; i < 36; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(200, ct).ConfigureAwait(false);
                if (HasSecondaryMonitor())
                    break;
                // 中途再补一次控制台，避免重载后管道又掉
                if (i is 8 or 18)
                    await MttVddInstaller.TryEnsureConsoleAsync(ct, waitAttempts: 4, delayMs: 400)
                        .ConfigureAwait(false);
            }
        }
        else
        {
            // 已有副屏：不要再发 SETDISPLAYCOUNT（会重载驱动、弄掉管道，下一轮检查又失败）
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        if (!HasSecondaryMonitor())
            return false;

        var sideHint = layoutSide switch
        {
            Services.ScreenExtend.LayoutEdgeSide.Left => "左侧",
            Services.ScreenExtend.LayoutEdgeSide.Right => "右侧",
            Services.ScreenExtend.LayoutEdgeSide.Top => "上方",
            Services.ScreenExtend.LayoutEdgeSide.Bottom => "下方",
            _ => null
        };
        progress?.Report(sideHint == null
            ? $"正在将副屏模式设为 {tw}x{th}…"
            : $"正在将副屏放到主屏{sideHint}（{tw}x{th}）…");
        // 仅临时改副屏模式/位置，不写注册表、不碰主屏 DPI
        DisplayModeHelper.TrySetSecondaryMode(tw, th, tr, layoutSide);

        return HasSecondaryMonitor();
    }

    /// <summary>兼容旧调用：副屏对齐主屏分辨率。</summary>
    public static bool TryAlignVddSettingsWithPrimary(DisplayModeHelper.PrimaryDisplayInfo? primary = null)
    {
        primary ??= DisplayModeHelper.GetPrimaryInfo();
        return TryAlignVddSettings(primary.Value.Width, primary.Value.Height, primary.Value.RefreshHz);
    }

    /// <summary>
    /// 写入 vdd_settings：count≥1、指定分辨率置顶、关闭 HardwareCursor（软件光标，扩展屏可见指针）。
    /// </summary>
    public static bool TryAlignVddSettings(int width, int height, int refreshHz)
    {
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;
        if (refreshHz <= 0) refreshHz = 60;
        try
        {
            var path = ResolveVddSettingsPath();
            if (path == null || !File.Exists(path)) return false;
            var doc = XDocument.Load(path);
            if (doc.Root == null) return false;

            var monitors = doc.Root.Element("monitors") ?? new XElement("monitors");
            if (monitors.Parent == null) doc.Root.Add(monitors);
            var countNode = monitors.Element("count") ?? new XElement("count", "1");
            if (countNode.Parent == null) monitors.Add(countNode);
            if (!int.TryParse(countNode.Value, out var c) || c < 1)
                countNode.Value = "1";

            var options = doc.Root.Element("options") ?? new XElement("options");
            if (options.Parent == null) doc.Root.Add(options);
            var hw = options.Element("HardwareCursor") ?? new XElement("HardwareCursor", "false");
            if (hw.Parent == null) options.Add(hw);
            // IDD 硬件光标在扩展虚拟屏上经常不可见；改用软件光标
            hw.Value = "false";

            var resolutions = doc.Root.Element("resolutions") ?? new XElement("resolutions");
            if (resolutions.Parent == null) doc.Root.Add(resolutions);

            // 去掉同尺寸旧项，再把目标分辨率插到最前（驱动优先用列表首项）
            foreach (var old in resolutions.Elements("resolution").ToList())
            {
                var w = (string?)old.Element("width");
                var h = (string?)old.Element("height");
                if (w == width.ToString() && h == height.ToString())
                    old.Remove();
            }

            var preferred = new XElement("resolution",
                new XElement("width", width),
                new XElement("height", height),
                new XElement("refresh_rate", Math.Clamp(refreshHz, 30, 244)));
            resolutions.AddFirst(preferred);

            doc.Save(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入 C:\VirtualDisplayDriver\vdd_settings.xml 的 monitors/count（若存在）。</summary>
    public static bool TrySetXmlMonitorCount(int count)
    {
        count = Math.Clamp(count, 0, 8);
        try
        {
            var path = ResolveVddSettingsPath();
            if (path == null || !File.Exists(path)) return false;
            var doc = XDocument.Load(path);
            var node = doc.Root?.Element("monitors")?.Element("count");
            if (node == null)
            {
                var monitors = doc.Root?.Element("monitors");
                if (monitors == null)
                {
                    if (doc.Root == null) return false;
                    monitors = new XElement("monitors");
                    doc.Root.Add(monitors);
                }

                monitors.Add(new XElement("count", count.ToString()));
            }
            else
            {
                node.Value = count.ToString();
            }

            doc.Save(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveVddSettingsPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
            var basePath = key?.GetValue("VDDPATH") as string;
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var p = Path.Combine(basePath.Trim(), "vdd_settings.xml");
                if (File.Exists(p)) return p;
            }
        }
        catch { /* ignore */ }

        var fallback = @"C:\VirtualDisplayDriver\vdd_settings.xml";
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>注册表/常见目录是否能看到 Virtual Display Driver 产品（不等于管道已就绪）。</summary>
    public static bool IsProductInstalled()
    {
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
                        var display = (sub?.GetValue("DisplayName") as string) ?? "";
                        if (LooksLikeVddProduct(display))
                            return true;
                    }
                }
            }

            foreach (var pf in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (string.IsNullOrWhiteSpace(pf) || !Directory.Exists(pf)) continue;
                foreach (var dir in Directory.EnumerateDirectories(pf))
                {
                    if (LooksLikeVddProduct(Path.GetFileName(dir)))
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
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

    /// <summary>
    /// 扩展模式环境是否就绪（只读探测，禁止改显示拓扑）。
    /// 切勿在此调用 EnsureVirtualMonitorVisibleAsync / DisplaySwitch——弹窗检测时反复改拓扑会卡死整机。
    /// 副屏创建推迟到用户点击「开始投影」。
    /// </summary>
    public static async Task<(bool Ready, string Detail)> CheckExtendReadyAsync(CancellationToken ct = default)
    {
        if (HasSecondaryMonitor())
        {
            var m = DisplayMonitorEnum.FindBestExtendTarget()!;
            return (true, $"已有副屏 {m.Value.Width}x{m.Value.Height}");
        }

        if (await IsDriverReadyAsync(ct).ConfigureAwait(false))
        {
            // 管道就绪即可通过检测；副屏在 Start→Ensure 时再创建，避免检测阶段 DisplaySwitch 拖垮桌面
            return (true, "虚拟显示驱动已就绪（开始投影时再创建副屏）");
        }

        // 停止投影时驱动重载会把 VDDControl 打退出，管道消失，但软件仍显示已安装。
        // 这里只拉起控制台并等待管道，不改数量、不切显示拓扑、不走重装。
        if (IsProductInstalled()
            || MttVddInstaller.HasAnyControlExecutable())
        {
            await MttVddInstaller.TryEnsureConsoleAsync(ct, waitAttempts: 12, delayMs: 700)
                .ConfigureAwait(false);
            if (HasSecondaryMonitor())
            {
                var again = DisplayMonitorEnum.FindBestExtendTarget()!.Value;
                return (true, $"已有副屏 {again.Width}x{again.Height}");
            }

            if (await IsDriverReadyAsync(ct).ConfigureAwait(false))
                return (true, "虚拟显示驱动已就绪（开始投影时再创建副屏）");

            return (false,
                "已安装 Virtual Display Driver，但未检测到控制台管道。请稍候再点「重新检测」，或手动运行 VddOffline\\VDDControl*.exe；不必重复自动修复安装");
        }

        return (false, "未检测到真副屏，且未安装或未运行虚拟显示驱动（MTT VDD）");
    }

    /// <summary>供目标下拉：本机副屏条目。</summary>
    public static IReadOnlyList<(string Id, string Label)> ListLocalSecondaryLabels()
    {
        var all = DisplayMonitorEnum.GetAll().Where(m => !m.IsPrimary).ToList();
        var list = new List<(string, string)>();
        for (var i = 0; i < all.Count; i++)
        {
            var m = all[i];
            var id = $"local-sec-{i}:{m.Left},{m.Top},{m.Width}x{m.Height}";
            list.Add((id, $"本机副屏 {m.Width}x{m.Height}"));
        }

        return list;
    }
}
