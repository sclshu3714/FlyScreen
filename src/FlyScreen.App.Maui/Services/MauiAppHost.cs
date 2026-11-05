using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// MAUI 跨平台业务宿主。Windows 兑现 DesktopFull；Android 兑现 AndroidMobile。
/// 不引用、不修改 FlyScreen.App.Windows。
/// </summary>
public sealed class MauiAppHost : IAsyncDisposable
{
    private static MauiAppHost? _instance;
    private int _started;
    private IMauiPlatformServices _platform = NullMauiPlatformServices.Instance;

    public static MauiAppHost Instance =>
        _instance ?? throw new InvalidOperationException("MauiAppHost 未创建");

    public static MauiAppHost EnsureCreated()
    {
        if (_instance != null) return _instance;
        _instance = new MauiAppHost();
        return _instance;
    }

    public AppSettings Settings { get; }
    public ConnectionManager Connections { get; }
    public LayoutSyncHelper LayoutSync { get; }
    public BehaviorSyncHelper BehaviorSync { get; }
    public SecurityKeySyncHelper SecurityKeySync { get; }
    public DisplayInfoService DisplayInfo { get; }
    public FileTransferService Transfers { get; }
    public EdgeFileDragService EdgeDrag { get; }
    public ScreenExtendService ScreenExtend { get; }
    public IMauiPlatformServices Platform => _platform;

    public event Action<string>? StatusMessage;

    /// <summary>投影冲突/被拒等需弹窗提示。</summary>
    public event Action<string>? ProjectionUserNotice;
    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? BehaviorSyncedFromPeer;
    public event Action<string>? SecurityKeySyncedFromPeer;

    public string PlatformBanner { get; }

    private MauiAppHost()
    {
#if ANDROID
        // 须在 Load/EnsureLocalMachineSlot 之前：否则会把 localhost 写入槽位
        LocalPeerIdentity.SetMachineNameResolver(
            Platforms.Android.AndroidDeviceIdentity.ResolveFriendlyName);
        NicService.PreferredIpv4Hint = Platforms.Android.AndroidNetworkBootstrap.TryGetWifiIpv4;
#endif
        var (platform, deviceClass, caps) = DetectIdentity();
        LocalPeerIdentity.Configure(platform: platform, deviceClass: deviceClass, caps: caps);
        PlatformBanner = $"{platform} · {deviceClass} · {LocalPeerIdentity.MachineName}";

        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Settings.Save(); // 持久化 localhost → 真实设备名 迁移
        Connections = new ConnectionManager(Settings);
        ProgramShareService.Attach(Connections);
        var sink = new MauiClipboardApplySink();
        Transfers = new FileTransferService(Settings, Connections, sink);
        EdgeDrag = new EdgeFileDragService(Settings, Connections, Transfers, ResolveEdgeDropDirectory);
        EdgeDrag.ConfirmConflict = ConfirmEdgeDragConflict;
        EdgeDrag.StatusMessage += PublishStatus;
        ScreenExtend = new ScreenExtendService(Connections);
        EdgeDrag.RejectPeer = peer => ScreenExtend.IsExcludedFromEdgeCross(peer);
        ScreenExtend.StatusMessage += PublishStatus;
        ScreenExtend.UserNotice += OnProjectionUserNotice;
#if !WINDOWS
        ScreenExtend.ShouldAcceptOffer = (_, _) => false;
#endif
        LayoutSync = new LayoutSyncHelper(Settings, Connections);
        LayoutSync.StatusMessage += PublishStatus;
        LayoutSync.LayoutSyncedFromPeer += peer =>
        {
            // 只通知 UI；MaintainLoop 负责补连/剪枝，避免反复 Restart 闪烁
            RestoreCursorIfLayoutSolo();
            LayoutSyncedFromPeer?.Invoke(peer);
        };
        LayoutSync.KickedFromLayout += () =>
        {
            _platform.RestoreCursorWhenLeavingCluster();
            LayoutSyncedFromPeer?.Invoke(LocalPeerIdentity.MachineName);
        };
        BehaviorSync = new BehaviorSyncHelper(Settings, Connections);
        BehaviorSync.StatusMessage += PublishStatus;
        BehaviorSync.BehaviorSyncedFromPeer += peer =>
        {
            _platform.SetEasyMouse(Settings.EasyMouse);
            BehaviorSyncedFromPeer?.Invoke(peer);
            // 行为同步不重启网络，避免打断已有连接与扩展屏等会话
        };
        SecurityKeySync = new SecurityKeySyncHelper(Settings, Connections, Connections.Discovery);
        SecurityKeySync.StatusMessage += PublishStatus;
        SecurityKeySync.SecurityKeyAppliedFromPeer += (from, _key) =>
        {
            try { SecurityKeySyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
            _ = RestartNetworkingAsync();
        };
        DisplayInfo = new DisplayInfoService(Connections);
#if WINDOWS
        DisplayInfo.GetLocalDisplayInfo = () => FlyScreen.ScreenExtend.DisplayModeHelper.ToDisplayInfoPayload();
#endif
        Connections.StatusMessage += PublishStatus;
        Transfers.StatusMessage += PublishStatus;
        Connections.MessageReceived += OnPeerMessage;

        Transfers.RemoteInlineAnnounced += () =>
        {
            if (_platform.IsFocusOnLocalMachine)
                _platform.ArmRemoteClipboardForPaste();
        };
        Transfers.RemoteFilesAnnounced += () =>
        {
            if (_platform.IsFocusOnLocalMachine)
                _platform.ArmRemoteClipboardForPaste();
        };
    }

    private static async Task<EdgeDragConflictAction> ConfirmEdgeDragConflict(EdgeDragConflictRequest request)
    {
        if (request.Conflicts.Count == 0) return EdgeDragConflictAction.Proceed;
#if WINDOWS
        // WinUI 主线程上 TaskDialog 常静默失败/返回怪码，表现成 Win→Maui「拖不过去」。
        // 与 WPF 端自绘对话框同目标：必须能真正弹出让用户选。
        return await MainThread.InvokeOnMainThreadAsync(() =>
            FlyScreen.Input.EdgeDragConflictDialog.AskMessageBox(request)).ConfigureAwait(false);
#else
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return EdgeDragConflictAction.Replace;
        var one = request.Conflicts.Count == 1;
        var dest = string.IsNullOrWhiteSpace(request.Directory) ? "收件箱" : Path.GetFileName(
            request.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var source = string.IsNullOrWhiteSpace(request.SourceFolder) ? "对端" : request.SourceFolder;
        var title = $"正在将 {Math.Max(request.TotalCount, request.Conflicts.Count)} 个项目从 {source} 复制到 {dest}";
        var instruction = one
            ? $"目标已包含一个名为“{request.Conflicts[0]}”的文件"
            : $"目标已包含 {request.Conflicts.Count} 个同名项目";
        while (true)
        {
            var choice = await page.DisplayActionSheetAsync(
                title + "\n" + instruction,
                "取消",
                null,
                "替换目标中的文件",
                one ? "跳过该文件" : "跳过这些文件",
                "比较两个文件的信息").ConfigureAwait(true);
            if (choice == "替换目标中的文件") return EdgeDragConflictAction.Replace;
            if (choice is "跳过该文件" or "跳过这些文件") return EdgeDragConflictAction.Skip;
            if (choice == "比较两个文件的信息")
            {
                await page.DisplayAlertAsync("比较两个文件的信息", BuildAndroidCompare(request), "关闭")
                    .ConfigureAwait(true);
                continue;
            }

            return EdgeDragConflictAction.Cancel;
        }
#endif
    }

#if !WINDOWS
    private static string BuildAndroidCompare(EdgeDragConflictRequest request)
    {
        var lines = new List<string>();
        foreach (var name in request.Conflicts.Take(8))
        {
            lines.Add(name);
            var src = request.SourceItems.FirstOrDefault(i =>
                string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            lines.Add("  正在复制：" + (src == null
                ? "未知"
                : src.IsDirectory
                    ? "文件夹"
                    : src.Size >= 0 ? $"{src.Size} 字节" : "未知"));
            try
            {
                var path = Path.Combine(request.Directory, name);
                if (File.Exists(path))
                    lines.Add($"  已有文件：{new FileInfo(path).Length} 字节");
                else if (Directory.Exists(path))
                    lines.Add("  已有：文件夹");
                else
                    lines.Add("  已有：未知");
            }
            catch
            {
                lines.Add("  已有：未知");
            }
        }

        return string.Join("\n", lines);
    }
#endif

    private string ResolveEdgeDropDirectory()
    {
#if WINDOWS
        return FlyScreen.Input.WindowsDropFolder.Resolve();
#else
        return Transfers.InboxDirectory;
#endif
    }

    /// <summary>由 MauiProgram / 平台启动代码注入 Windows/Android 实现。</summary>
    public void AttachPlatform(IMauiPlatformServices platform)
    {
        _platform.StopDesktopInput();
        _platform.StopClipboardMonitor();
        _platform.DisposeTray();
        _platform = platform ?? NullMauiPlatformServices.Instance;
        Transfers.SetClipboardSink(_platform.ClipboardSink);
        _platform.AttachEdgeFileDrag(EdgeDrag);
        _platform.ControlTargetChanged += peer =>
        {
            if (peer == null)
                _platform.ArmRemoteClipboardForPaste();
        };

        // 对齐 Win App：按设置同步开机自启（关闭则再清一次残留任务）
        if (_platform.SupportsAutoStart)
        {
            _platform.SetAutoStartEnabled(Settings.AutoStart);
            var actual = _platform.GetAutoStartEnabled();
            if (Settings.AutoStart != actual)
            {
                Settings.AutoStart = actual;
                Settings.Save();
            }
        }
    }

    private static (string Platform, string DeviceClass, string[] Caps) DetectIdentity()
    {
#if WINDOWS
        return (PeerPlatform.Windows, PeerDeviceClass.Desktop, PeerCaps.DesktopFull);
#elif ANDROID
        return DeviceInfo.Idiom == DeviceIdiom.Tablet
            ? (PeerPlatform.Android, PeerDeviceClass.Tablet, PeerCaps.AndroidMobile)
            : (PeerPlatform.Android, PeerDeviceClass.Phone, PeerCaps.AndroidMobile);
#elif MACCATALYST || IOS
        return (PeerPlatform.MacOS, PeerDeviceClass.Desktop, PeerCaps.MacDesktopW1);
#else
        return ("Unknown", PeerDeviceClass.Unknown, PeerCaps.AndroidMobile);
#endif
    }

    private void PublishStatus(string message)
    {
        AppLog.RecordStatus(message);
        StatusMessage?.Invoke(AppLog.ForStatusBar(message));
    }

    private void OnProjectionUserNotice(string message)
    {
        PublishStatus(message);
        try { ProjectionUserNotice?.Invoke(message); } catch { /* ignore */ }
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
#if ANDROID
        Platforms.Android.AndroidNetworkBootstrap.EnsureMulticastLock();
#endif
        Settings.EnsureLocalMachineSlot();
#if WINDOWS
        // 上次扩展投影异常退出：补 /internal 收拢仅电脑屏幕
        try
        {
            await FlyScreen.ScreenExtend.DisplayTopologyGuard.TryRecoverOrphanedExtendAsync()
                .ConfigureAwait(false);
        }
        catch { /* ignore */ }

        try
        {
            var fw = await MauiConnectionDiagnostics.EnsureListenRulesAsync(Settings.TcpPort, Settings.UdpPort)
                .ConfigureAwait(false);
            PublishStatus(fw.Message);
        }
        catch (Exception ex)
        {
            PublishStatus("配置防火墙失败：" + ex.Message);
        }
#endif
        await Connections.StartAsync().ConfigureAwait(false);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_platform.SupportsDesktopInput)
            {
                _platform.SetEasyMouse(Settings.EasyMouse);
                _platform.StartDesktopInput();
            }

            _platform.StartClipboardMonitor();
        });
        PublishStatus($"网络已启动 · {PlatformBanner} · {Connections.BindIp}");
    }

    /// <summary>强制整网重启（Stop+Start）。仅端口/网卡/密钥等监听参数变更时使用。</summary>
    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
#if WINDOWS
        try
        {
            var fw = await MauiConnectionDiagnostics.EnsureListenRulesAsync(Settings.TcpPort, Settings.UdpPort)
                .ConfigureAwait(false);
            PublishStatus(fw.Message);
        }
        catch (Exception ex)
        {
            PublishStatus("配置防火墙失败：" + ex.Message);
        }
#endif
        await Connections.StartAsync().ConfigureAwait(false);
        _platform.SetEasyMouse(Settings.EasyMouse);
    }

    /// <summary>
    /// 应用设置后的网络侧：默认保持现有连接，只剪枝并补连；
    /// 仅当 TCP/UDP 端口、网卡/绑定 IP、安全密钥变化时才整网重启。
    /// </summary>
    public async Task ApplyNetworkingAsync(
        int previousTcpPort,
        int previousUdpPort,
        string previousNicId,
        string previousLocalIp,
        string previousSecurityKey)
    {
        Settings.Save();
        var needRestart =
            previousTcpPort != Settings.TcpPort
            || previousUdpPort != Settings.UdpPort
            || !string.Equals(previousNicId ?? "", Settings.PreferredNicId ?? "", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previousLocalIp ?? "", Settings.PreferredLocalIp ?? "", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previousSecurityKey ?? "", Settings.SecurityKey ?? "", StringComparison.Ordinal);

        if (needRestart || !Connections.IsRunning)
            await Connections.StartAsync().ConfigureAwait(false);
        else
            await Connections.RefreshConfiguredPeersAsync().ConfigureAwait(false);

        _platform.SetEasyMouse(Settings.EasyMouse);
    }

    public Task<DisplayInfoPayload?> QueryPeerDisplayAsync(string peer, CancellationToken ct = default)
        => DisplayInfo.QueryPeerAsync(peer, ct: ct);

    public DisplayInfoPayload? TryGetLocalDisplayInfo()
    {
#if WINDOWS
        return FlyScreen.ScreenExtend.DisplayModeHelper.ToDisplayInfoPayload();
#elif ANDROID
        try
        {
            var d = DeviceDisplay.MainDisplayInfo;
            var density = d.Density <= 0 ? 1 : d.Density;
            return new DisplayInfoPayload
            {
                Width = Math.Max(1, (int)d.Width),
                Height = Math.Max(1, (int)d.Height),
                ScalePercent = (int)Math.Round(density * 100),
                RefreshHz = d.RefreshRate > 1 ? (int)Math.Round(d.RefreshRate) : 60
            };
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }

    public async Task<ProjectionStartResult> StartProjectionAsync(
        ProjectionTarget? target,
        DisplayProjectionMode mode,
        ProjectionDisplayChoice? display = null,
        bool audioSync = false,
        bool remoteControl = false,
        IProgress<string>? progress = null)
    {
#if !WINDOWS && !ANDROID
        await Task.CompletedTask;
        return ProjectionStartResult.Fail("当前平台不支持发起投影");
#else
#if ANDROID
        // 手机仅复制；扩展需虚拟副屏，Android 不做
        mode = DisplayProjectionMode.Clone;
#else
        mode = DisplayProjectionModeInfo.ClampForPicker((int)mode);
#endif
        progress?.Report("① 校验投影目标…");
        if (target == null)
            return ProjectionStartResult.Fail("请选择目标");

        string peer;
        if (target.Kind == ProjectionTargetKind.Peer)
        {
            peer = target.Id.Trim();
            if (string.IsNullOrEmpty(peer))
                return ProjectionStartResult.Fail("请选择目标电脑");
            if (!Connections.GetPeerStatus(peer).Bidirectional)
                return ProjectionStartResult.Fail($"「{peer}」未双向连接，无法投影");
            if (!ScreenExtendTargets.IsEligiblePeer(Connections, peer))
                return ProjectionStartResult.Fail($"「{peer}」不是可投影的电脑/大屏");
            Settings.ScreenExtendPeer = peer;
        }
        else
        {
#if ANDROID
            return ProjectionStartResult.Fail("手机端请选择一台已连接的电脑作为投影目标");
#else
            peer = (Settings.ScreenExtendPeer ?? "").Trim();
            if (string.IsNullOrEmpty(peer) || !Connections.GetPeerStatus(peer).Bidirectional)
                peer = ScreenExtendTargets.ListEligible(Connections).FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(peer))
                return ProjectionStartResult.Fail("已选本机副屏，但仍需至少一台双向已连接电脑作为接收端");
#endif
        }

        var sameHostPeer = ScreenExtend.Role == ScreenExtendRole.Host
            && string.Equals(ScreenExtend.Peer, peer, StringComparison.OrdinalIgnoreCase);
        if (ScreenExtend.IsActive && !sameHostPeer)
        {
            return ProjectionStartResult.Fail(
                ScreenExtendConflict.DescribeLocalBusy(ScreenExtend.Role, ScreenExtend.Peer));
        }

        var choice = display ?? new ProjectionDisplayChoice
        {
            Source = mode == DisplayProjectionMode.Clone
                ? ProjectionDisplaySource.HostPrimary
                : ProjectionDisplaySource.TargetPeer,
            Width = 1920,
            Height = 1080,
            ScalePercent = 100
        };
        if (choice.Width <= 0 || choice.Height <= 0)
        {
            var local = TryGetLocalDisplayInfo();
            if (local != null)
            {
                choice.Width = local.Width;
                choice.Height = local.Height;
                choice.ScalePercent = local.ScalePercent;
                choice.RefreshHz = local.RefreshHz;
            }
        }

        choice.ScalePercent = ProjectionDisplayPresets.ClampScale(
            choice.ScalePercent > 0 ? choice.ScalePercent : 100);
        Settings.ScreenExtendProjectionMode = (int)mode;
        ProjectionDisplayPresets.WriteRemembered(Settings, choice, audioSync, remoteControl);
        Settings.Save();
        Platform.SetScreenExtendProjectionMode(mode);
        Platform.SetScreenExtendDisplayChoice(choice);

#if WINDOWS
        // 任何 DisplaySwitch / 改模式前先快照主屏
        FlyScreen.ScreenExtend.DisplayTopologyGuard.CaptureIfEmpty();

        if (mode == DisplayProjectionMode.Extend)
        {
            progress?.Report("② 检查扩展环境（安装就绪）…");
            if (target.Kind == ProjectionTargetKind.Peer)
            {
                var (ready, detail) = await FlyScreen.ScreenExtend.MttVddEnvironment.CheckExtendReadyAsync()
                    .ConfigureAwait(false);
                if (!ready)
                    return ProjectionStartResult.NeedRepair(detail);
            }

            progress?.Report("③ 创建虚拟显示通道并配置拓扑…");
            var layoutSide = target.Kind == ProjectionTargetKind.Peer
                ? LayoutPeerDirection.Resolve(
                    Settings.MachineSlots,
                    Settings.TwoRow,
                    LocalPeerIdentity.MachineName,
                    peer,
                    Environment.MachineName)
                : LayoutEdgeSide.Unknown;
            var (dispOk, dispInfo) = await Platform.EnsureScreenExtendHostDisplayAsync(layoutSide)
                .ConfigureAwait(false);
            if (!dispOk)
            {
                // 已装驱动但通道拉不起（黄叹号/冲突/控制台未起）→ 可自动修复
                var canRepair = FlyScreen.ScreenExtend.MttVddEnvironment.IsProductInstalled()
                    || FlyScreen.ScreenExtend.MttVddInstaller.HasAnyControlExecutable();
                return canRepair
                    ? ProjectionStartResult.NeedRepair(dispInfo ?? "无法创建第二输出（通道）")
                    : ProjectionStartResult.Fail(dispInfo ?? "无法创建第二输出（通道）");
            }
            PublishStatus($"投影：本机第二输出已就绪 {dispInfo}"
                          + (layoutSide == LayoutEdgeSide.Unknown
                              ? ""
                              : $"，方位={layoutSide}"));
            // Ensure 内已按需 DisplaySwitch 一次；勿再重复 /extend
        }
        else
        {
            progress?.Report("② 切换为复制模式…");
            FlyScreen.ScreenExtend.DisplayProjection.TryApply(DisplayProjectionMode.Clone);
        }
#endif

#if ANDROID
        progress?.Report("② 申请屏幕录制…");
        // 须先拿到系统录屏授权并启动采集，再发 Offer（Accept 后即可推帧）
        var (capOk, capErr) = await Platform.PrepareScreenExtendCaptureAsync().ConfigureAwait(false);
        if (!capOk)
            return ProjectionStartResult.Fail(capErr ?? "屏幕录制授权失败");
#endif

        if (sameHostPeer
            && ScreenExtend.AudioSync == audioSync
            && ScreenExtend.RemoteControl == remoteControl)
        {
            progress?.Report("⑤ 投影已开始");
            PublishStatus($"投影：已更新为「{DisplayProjectionModeInfo.Label(mode)}」→ {peer}");
            return ProjectionStartResult.Success();
        }

        if (ScreenExtend.IsActive)
        {
            if (!sameHostPeer)
            {
                return ProjectionStartResult.Fail(
                    ScreenExtendConflict.DescribeLocalBusy(ScreenExtend.Role, ScreenExtend.Peer));
            }

            await ScreenExtend.StopAsync("retarget").ConfigureAwait(false);
        }

        progress?.Report("④ 向目标发起投影会话…");
        var (ok, err) = await ScreenExtend.StartHostAsync(
                peer, choice.Width, choice.Height, audioSync: audioSync, remoteControl: remoteControl)
            .ConfigureAwait(false);
        if (!ok)
        {
#if ANDROID
            await Platform.ReleaseScreenExtendHostDisplayAsync().ConfigureAwait(false);
#endif
            return ProjectionStartResult.Fail(err ?? "发起投影失败");
        }

        progress?.Report("⑤ 投影已开始");
        PublishStatus(
            $"投影：已向 {peer} 发起（{DisplayProjectionModeInfo.Label(mode)} · {choice.ResolutionLabel} @{choice.ScaleLabel}"
            + (audioSync ? " · 声音同步" : "")
            + (remoteControl ? " · 远程控制" : "")
            + (choice.CropBlackBars ? " · 去黑边" : "")
            + "）");
        return ProjectionStartResult.Success();
#endif
    }

    public async Task StopProjectionAsync()
    {
#if WINDOWS || ANDROID
        if (ScreenExtend.IsActive)
            await ScreenExtend.StopAsync("stopped").ConfigureAwait(false);
        await Platform.ReleaseScreenExtendHostDisplayAsync().ConfigureAwait(false);
#if WINDOWS
        PublishStatus("投影：已停止（已切回仅电脑屏幕）");
#else
        PublishStatus("投影：已停止");
#endif
#else
        await Task.CompletedTask;
#endif
    }

    public async Task<(bool Ok, string Message)> RepairProjectionEnvironmentAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
#if WINDOWS
        var result = await FlyScreen.ScreenExtend.MttVddRepairSession
            .RunOrAttachAsync(progress, ct).ConfigureAwait(false);
        SyncMttInstallFlag();
        return result;
#else
        await Task.CompletedTask;
        return (false, "仅 Windows 支持");
#endif
    }

    public bool IsProjectionEnvironmentRepairing
#if WINDOWS
        => FlyScreen.ScreenExtend.MttVddRepairSession.IsRunning;
#else
        => false;
#endif

    public string ProjectionEnvironmentRepairStatus
#if WINDOWS
        => FlyScreen.ScreenExtend.MttVddRepairSession.Status;
#else
        => "";
#endif

    public async Task<(bool Ok, string Message)> UninstallProjectionEnvironmentAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
#if WINDOWS
        if (ScreenExtend.IsActive)
            await StopProjectionAsync().ConfigureAwait(false);
        var result = await FlyScreen.ScreenExtend.MttVddInstaller.TryUninstallAsync(progress, ct)
            .ConfigureAwait(false);
        SyncMttInstallFlag();
        return result;
#else
        await Task.CompletedTask;
        return (false, "仅 Windows 支持");
#endif
    }

    public bool IsMttInstalledByApp
    {
        get
        {
#if WINDOWS
            SyncMttInstallFlag();
            return FlyScreen.ScreenExtend.MttVddInstallRecord.IsInstalledByApp;
#else
            return false;
#endif
        }
    }

#if WINDOWS
    private void SyncMttInstallFlag()
    {
        var byApp = FlyScreen.ScreenExtend.MttVddInstallRecord.IsInstalledByApp;
        if (Settings.MttVddInstalledByApp == byApp) return;
        Settings.MttVddInstalledByApp = byApp;
        Settings.Save();
    }
#endif


    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastReplaceAsync();

    public Task BroadcastLayoutMergeAsync() => LayoutSync.BroadcastMergeAsync();

    public Task BroadcastBehaviorSyncAsync() => BehaviorSync.BroadcastAsync();

    public Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default)
        => SecurityKeySync.SyncCurrentKeyAsync(ct);

    /// <summary>布局仅剩本机时恢复可见光标。</summary>
    public void RestoreCursorIfLayoutSolo()
    {
        if (LayoutSlotMerge.IsOnlyLocal(Settings.MachineSlots, LocalPeerIdentity.MachineName))
            _platform.RestoreCursorWhenLeavingCluster();
    }

    public void ApplyFromUi(
        string securityKey,
        IReadOnlyList<string> slots,
        bool twoRow,
        int tcpPort,
        int udpPort,
        bool wrapMouse,
        bool shareClipboard,
        bool transferFile,
        bool easyMouse,
        bool sameSubnet,
        bool hideMouseAtEdge = true,
        bool drawMouseCursor = true,
        bool showStatusMessages = true,
        bool autoStart = false,
        bool blockScreenSaver = true,
        bool moveMouseRelatively = false,
        bool blockMouseAtCorners = false,
        int switchHotkeyMode = 0,
        string? hotkeySettings = null,
        string? hotkeyLock = null,
        string? hotkeyReconnect = null,
        string? hotkeyExit = null,
        string? uiTheme = null,
        string? uiLanguage = null,
        int edgeCrossMode = 0)
    {
        Settings.SecurityKey = securityKey.Trim();
        Settings.MachineSlots = slots.Select(s => s.Trim()).ToList();
        while (Settings.MachineSlots.Count < 4) Settings.MachineSlots.Add("");
        Settings.TwoRow = twoRow;
        Settings.TcpPort = tcpPort;
        Settings.UdpPort = udpPort;
        Settings.WrapMouse = wrapMouse;
        Settings.ShareClipboard = shareClipboard;
        Settings.TransferFile = transferFile;
        Settings.EasyMouse = easyMouse;
        Settings.EdgeCrossMode = edgeCrossMode is >= 0 and <= 2 ? edgeCrossMode : 0;
        Settings.SameSubnetOnly = sameSubnet;
        Settings.HideMouseAtEdge = hideMouseAtEdge;
        Settings.DrawMouseCursor = drawMouseCursor;
        Settings.ShowStatusMessages = showStatusMessages;
        Settings.AutoStart = autoStart;
        Settings.BlockScreenSaver = blockScreenSaver;
        Settings.MoveMouseRelatively = moveMouseRelatively;
        Settings.BlockMouseAtCorners = blockMouseAtCorners;
        Settings.SwitchHotkeyMode = switchHotkeyMode;
        if (hotkeySettings != null) Settings.HotkeySettings = hotkeySettings;
        if (hotkeyLock != null) Settings.HotkeyLock = hotkeyLock;
        if (hotkeyReconnect != null) Settings.HotkeyReconnect = hotkeyReconnect;
        if (hotkeyExit != null) Settings.HotkeyExit = hotkeyExit;
        if (!string.IsNullOrWhiteSpace(uiTheme)) Settings.UiTheme = uiTheme;
        if (!string.IsNullOrWhiteSpace(uiLanguage)) Settings.UiLanguage = uiLanguage;
        Settings.EnsureLocalMachineSlot();

        if (_platform.SupportsAutoStart)
        {
            if (_platform.SetAutoStartEnabled(autoStart))
                Settings.AutoStart = autoStart;
            else
            {
                // 写计划任务失败则与真实状态对齐，避免「取消了但下次又被 OR 写回」
                Settings.AutoStart = _platform.GetAutoStartEnabled();
            }
        }

        Settings.Save();
        _platform.SetEasyMouse(easyMouse);
        RestoreCursorIfLayoutSolo();
    }

    private void OnPeerMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.MouseMove:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseMovePayload>(msg.Payload);
                if (p == null) return;
                if (p.Relative) _platform.InjectMouseMoveRelative(p.Dx, p.Dy);
                else _platform.InjectMouseMoveAbsolute(p.Nx, p.Ny);
                break;
            }
            case MessageType.MouseButton:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p == null) return;
                _platform.InjectMouseButton(p.Button, p.Down);
                break;
            }
            case MessageType.MouseWheel:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p == null) return;
                _platform.InjectMouseWheel(p.Delta, p.Horizontal);
                break;
            }
            case MessageType.KeyEvent:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<KeyEventPayload>(msg.Payload);
                if (p == null) return;
                if (!string.IsNullOrEmpty(p.Text))
                    _platform.InjectText(p.Text);
                else
                    _platform.InjectKey(p.VirtualKey, p.Down, p.Extended);
                break;
            }
            case MessageType.CompositionUpdate:
            {
                // 组字浮层：尽量进入被控，但不因门闩丢掉拼音/候选（编辑器页尤甚）
                _ = EnsureAcceptsRemoteInput(peer);
                var p = MessageCodec.DecodeJson<CompositionUpdatePayload>(msg.Payload);
                if (p == null) return;
                RemoteCompositionOverlay.Show(peer, p);
                break;
            }
            case MessageType.CompositionCancel:
            {
                RemoteCompositionOverlay.Hide();
                break;
            }
            case MessageType.TextInput:
            {
                var p = MessageCodec.DecodeJson<TextInputPayload>(msg.Payload);
                if (p == null || string.IsNullOrEmpty(p.Text)) return;
                RemoteCompositionOverlay.Hide();
                // InjectText 内部优先写入程序编辑器 sink，不依赖 AcceptsRemoteInput
                _ = EnsureAcceptsRemoteInput(peer);
                _platform.InjectText(p.Text);
                break;
            }
            case MessageType.SwitchFocus:
            {
                var p = MessageCodec.DecodeJson<SwitchFocusPayload>(msg.Payload);
                if (p == null) return;
                // BeginReceiving 在触控板屏蔽态会直接拒绝，不进入被控
                if (IsLocalName(p.TargetMachine))
                    _platform.BeginReceivingRemoteControl(p.EntryX, p.EntryY);
                break;
            }
            case MessageType.CursorOwner:
            {
                var p = MessageCodec.DecodeJson<CursorOwnerPayload>(msg.Payload);
                if (p == null) return;
                _platform.OnCursorOwnerChanged(p.OwnerMachine);
                break;
            }
        }
    }

    /// <summary>
    /// 桌面 EdgeMouse 须 SwitchFocus；手机 AbsPointer 可能漏发 SwitchFocus，
    /// 在本机未控远端时自动进入被控，避免触控板键鼠被静默丢弃。
    /// </summary>
    private bool EnsureAcceptsRemoteInput(string peer)
    {
        if (_platform.AcceptsRemoteInput) return true;
        // 手机无桌面钩子，但仍可自动进入被控以收组字/TextInput（旧逻辑直接 return false 会丢候选）
        if (!_platform.IsFocusOnLocalMachine) return false;
        if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)
            && _platform.SupportsDesktopInput)
            return false;
        try
        {
            _platform.BeginReceivingRemoteControl(0.5, 0.5);
        }
        catch
        {
            return false;
        }
        return _platform.AcceptsRemoteInput;
    }

    private static bool IsLocalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopProjectionAsync().ConfigureAwait(false); }
        catch { /* ignore */ }

        _platform.StopClipboardMonitor();
        _platform.StopDesktopInput();
        _platform.DisposeTray();
#if WINDOWS
        if (_platform is Platforms.Windows.WindowsMauiPlatformServices winPlat)
        {
            try { winPlat.DisposeScreenExtendUi(); } catch { /* ignore */ }
        }
#endif
        ScreenExtend.Dispose();
        EdgeDrag.Dispose();
        LayoutSync.Detach();
        BehaviorSync.Detach();
        SecurityKeySync.Dispose();
        DisplayInfo.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
