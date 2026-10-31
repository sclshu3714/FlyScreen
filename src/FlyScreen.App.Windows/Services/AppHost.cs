using System.Windows.Threading;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Services;

public sealed class AppHost : IAsyncDisposable
{
    public static AppHost Instance { get; private set; } = null!;

    public AppSettings Settings { get; }
    public ConnectionManager Connections { get; }
    public InputRouter Input { get; }
    public FileTransferService Transfers { get; }
    public ClipboardMonitor Clipboard { get; }
    public ScreenExtendService ScreenExtend { get; }
    public ScreenExtendController ScreenExtendUi { get; }

    public event Action<string>? StatusMessage;
    /// <summary>收到对端布局并已写入设置（UI 需刷新矩阵）。</summary>
    public event Action<string>? LayoutSyncedFromPeer;
    /// <summary>收到对端功能/行为并已写入设置（UI 需刷新选项）。</summary>
    public event Action<string>? BehaviorSyncedFromPeer;
    /// <summary>收到对端安全密钥并已写入设置（UI 需刷新密钥框）。</summary>
    public event Action<string>? SecurityKeySyncedFromPeer;

    private BehaviorSyncHelper? _behaviorSync;
    private LayoutSyncHelper? _layoutSync;
    private SecurityKeySyncHelper? _securityKeySync;
    private DisplayInfoService? _displayInfo;

    private AppHost()
    {
        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        ProgramShareService.Attach(Connections);
        Input = new InputRouter(Settings, Connections);
        Transfers = new FileTransferService(Settings, Connections, new WindowsClipboardApplySink());
        Clipboard = new ClipboardMonitor(Settings, Transfers);
        ScreenExtend = new ScreenExtendService(Connections);
        ScreenExtendUi = new ScreenExtendController(ScreenExtend, Dispatcher.CurrentDispatcher);
        ScreenExtend.StatusMessage += PublishStatus;
        Input.IsEdgeCrossExcluded = peer => ScreenExtend.IsExcludedFromEdgeCross(peer);
        Input.ShouldPauseEasyMouseForExtend = () => ScreenExtend.ShouldPauseLocalEasyMouse;
        ScreenExtend.SessionEnded += () =>
        {
            try { Input.EasyMouseEnabled = Settings.EasyMouse; } catch { /* ignore */ }
        };
        ScreenExtend.SinkSessionReady += () =>
        {
            try { Input.EasyMouseEnabled = false; } catch { /* ignore */ }
        };
        Input.TryHandleLocalFilePasteHotkey = () => Clipboard.TryHandlePasteHotkey();
        Input.PrefetchRemoteFilesForLikelyPaste = () => Clipboard.PrefetchForLikelyPaste();
        _behaviorSync = new BehaviorSyncHelper(Settings, Connections);
        _behaviorSync.StatusMessage += PublishStatus;
        _behaviorSync.BehaviorSyncedFromPeer += from =>
        {
            Input.EasyMouseEnabled = Settings.EasyMouse;
            Input.RefreshEdgeCrossFromSettings();
            try { BehaviorSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
        };
        _layoutSync = new LayoutSyncHelper(Settings, Connections);
        _layoutSync.StatusMessage += PublishStatus;
        _layoutSync.LayoutSyncedFromPeer += from =>
        {
            // 只刷新 UI；由 MaintainLoop 补连/剪枝，避免整网重启导致状态来回闪
            RestoreCursorIfLayoutSolo();
            try { LayoutSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
        };
        _layoutSync.KickedFromLayout += () =>
        {
            // 被踢出布局、断开前：先恢复本机光标
            Input.RestoreCursorWhenLeavingCluster();
            try { LayoutSyncedFromPeer?.Invoke(LocalPeerIdentity.MachineName); } catch { /* ignore */ }
        };
        _securityKeySync = new SecurityKeySyncHelper(Settings, Connections, Connections.Discovery);
        _securityKeySync.StatusMessage += PublishStatus;
        _securityKeySync.SecurityKeyAppliedFromPeer += (from, _key) =>
        {
            try { SecurityKeySyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
            _ = RestartNetworkingAsync();
        };
        _displayInfo = new DisplayInfoService(Connections)
        {
            GetLocalDisplayInfo = () => FlyScreen.ScreenExtend.DisplayModeHelper.ToDisplayInfoPayload()
        };

        Connections.StatusMessage += PublishStatus;
        Transfers.StatusMessage += PublishStatus;
        Connections.MessageReceived += OnPeerMessage;

        // 鼠标回到本机：文件挂占位启用粘贴（粘贴时再传）；内联仍后台拉取
        Input.ControlTargetChanged += peer =>
        {
            if (peer == null)
                Clipboard.ArmRemoteClipboardForPaste();
        };

        // 焦点已在本机时对端才复制：立即挂占位 / 拉内联
        Transfers.RemoteFilesAnnounced += () =>
        {
            if (Input.IsFocusOnLocalMachine)
                Clipboard.ArmRemoteClipboardForPaste();
        };
        Transfers.RemoteInlineAnnounced += () =>
        {
            if (Input.IsFocusOnLocalMachine)
                Clipboard.ArmRemoteClipboardForPaste();
        };
    }

    private void PublishStatus(string message)
    {
        AppLog.RecordStatus(message);
        StatusMessage?.Invoke(AppLog.ForStatusBar(message));
    }

    public static AppHost EnsureCreated()
    {
        Instance ??= new AppHost();
        return Instance;
    }

    public async Task StartAsync()
    {
        await EnsureWindowsFirewallAsync().ConfigureAwait(false);
        await Connections.StartAsync().ConfigureAwait(false);
        Input.EasyMouseEnabled = Settings.EasyMouse;
        Input.Start();
    }

    /// <summary>强制整网重启（Stop+Start）。仅端口/网卡/密钥等监听参数变更时使用。</summary>
    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await EnsureWindowsFirewallAsync().ConfigureAwait(false);
        await Connections.StartAsync().ConfigureAwait(false);
    }

    private async Task EnsureWindowsFirewallAsync()
    {
        try
        {
            var (_, msg) = await new ConnectionDiagnostics(this).EnsureListenRulesAsync().ConfigureAwait(false);
            PublishStatus(msg);
        }
        catch (Exception ex)
        {
            PublishStatus("配置防火墙失败：" + ex.Message);
        }
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
    }

    /// <summary>
    /// 开始投影：复制推主屏；扩展需真副屏或 MTT。缺环境时返回 NeedEnvironmentRepair。
    /// </summary>
    public async Task<ProjectionStartResult> StartProjectionAsync(
        ProjectionTarget? target,
        DisplayProjectionMode mode,
        ProjectionDisplayChoice? display = null)
    {
        mode = DisplayProjectionModeInfo.ClampForPicker((int)mode);
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
            // 真副屏：采集该屏；接收端取最近一台双向电脑
            peer = (Settings.ScreenExtendPeer ?? "").Trim();
            if (string.IsNullOrEmpty(peer) || !Connections.GetPeerStatus(peer).Bidirectional)
            {
                peer = ScreenExtendTargets.ListEligible(Connections).FirstOrDefault() ?? "";
            }

            if (string.IsNullOrEmpty(peer))
                return ProjectionStartResult.Fail("已选本机副屏，但仍需至少一台双向已连接电脑作为接收端");
        }

        var choice = ResolveDisplayChoice(mode, display, peer);
        Settings.ScreenExtendProjectionMode = (int)mode;
        Settings.ScreenExtendDisplaySource = (int)choice.Source;
        Settings.ScreenExtendWidth = choice.Width;
        Settings.ScreenExtendHeight = choice.Height;
        Settings.ScreenExtendScalePercent = choice.ScalePercent;
        Settings.Save();
        ScreenExtendUi.SetProjectionMode(mode);
        ScreenExtendUi.SetDisplayChoice(choice);

        // 任何 DisplaySwitch / 改模式前先快照主屏
        FlyScreen.ScreenExtend.DisplayTopologyGuard.CaptureIfEmpty();

        if (mode == DisplayProjectionMode.Extend)
        {
            if (target.Kind == ProjectionTargetKind.Peer)
            {
                // 扩展 + 对端设备 → 虚拟屏路径：检查真副屏或 MTT
                var (ready, detail) = await FlyScreen.ScreenExtend.MttVddEnvironment.CheckExtendReadyAsync()
                    .ConfigureAwait(false);
                if (!ready)
                    return ProjectionStartResult.NeedRepair(detail);
            }
            else if (!FlyScreen.ScreenExtend.MttVddEnvironment.HasSecondaryMonitor())
            {
                return ProjectionStartResult.Fail("未找到本机副屏，请刷新或改选电脑目标");
            }

            var layoutSide = target.Kind == ProjectionTargetKind.Peer
                ? LayoutPeerDirection.Resolve(
                    Settings.MachineSlots,
                    Settings.TwoRow,
                    LocalPeerIdentity.MachineName,
                    peer,
                    Environment.MachineName)
                : LayoutEdgeSide.Unknown;
            var (dispOk, dispInfo) = await ScreenExtendUi.EnsureHostDisplayAsync(layoutSide: layoutSide)
                .ConfigureAwait(false);
            if (!dispOk)
                return ProjectionStartResult.NeedRepair(dispInfo ?? "无法创建第二输出");
            PublishStatus($"投影：本机第二输出已就绪 {dispInfo}"
                          + (layoutSide == LayoutEdgeSide.Unknown
                              ? ""
                              : $"，方位={layoutSide}"));
            // EnsureHostDisplay 内已按需 DisplaySwitch 一次；此处勿再 /extend（重复调用会卡死显示栈）
        }
        else
        {
            FlyScreen.ScreenExtend.DisplayProjection.TryApply(DisplayProjectionMode.Clone);
        }

        if (ScreenExtend.Role == ScreenExtendRole.Host
            && string.Equals(ScreenExtend.Peer, peer, StringComparison.OrdinalIgnoreCase))
        {
            PublishStatus($"投影：已更新为「{DisplayProjectionModeInfo.Label(mode)}」→ {peer}");
            return ProjectionStartResult.Success();
        }

        if (ScreenExtend.IsActive)
            await ScreenExtend.StopAsync("retarget").ConfigureAwait(false);

        var (ok, err) = await ScreenExtend.StartHostAsync(peer, choice.Width, choice.Height)
            .ConfigureAwait(false);
        if (!ok)
            return ProjectionStartResult.Fail(err ?? "发起投影失败");

        PublishStatus(
            $"投影：已向 {peer} 发起（{DisplayProjectionModeInfo.Label(mode)} · {choice.ResolutionLabel} @{choice.ScaleLabel}）");
        return ProjectionStartResult.Success();
    }

    private ProjectionDisplayChoice ResolveDisplayChoice(
        DisplayProjectionMode mode,
        ProjectionDisplayChoice? display,
        string peer)
    {
        if (display != null && display.Width > 0 && display.Height > 0)
        {
            display.ScalePercent = ProjectionDisplayPresets.ClampScale(
                display.ScalePercent > 0 ? display.ScalePercent : 100);
            return display;
        }

        if (mode == DisplayProjectionMode.Clone)
        {
            var local = GetLocalDisplayInfo();
            return new ProjectionDisplayChoice
            {
                Source = ProjectionDisplaySource.HostPrimary,
                Width = local.Width,
                Height = local.Height,
                ScalePercent = local.ScalePercent,
                RefreshHz = local.RefreshHz
            };
        }

        // 扩展：尽量用已缓存设置或本机主屏作占位；Accept 后可按 TargetPeer 再精调
        if (Settings.ScreenExtendWidth > 0 && Settings.ScreenExtendHeight > 0
            && Settings.ScreenExtendDisplaySource == (int)ProjectionDisplaySource.Custom)
        {
            return new ProjectionDisplayChoice
            {
                Source = ProjectionDisplaySource.Custom,
                Width = Settings.ScreenExtendWidth,
                Height = Settings.ScreenExtendHeight,
                ScalePercent = ProjectionDisplayPresets.ClampScale(
                    Settings.ScreenExtendScalePercent > 0 ? Settings.ScreenExtendScalePercent : 100)
            };
        }

        var primary = GetLocalDisplayInfo();
        return new ProjectionDisplayChoice
        {
            Source = ProjectionDisplaySource.TargetPeer,
            Width = primary.Width,
            Height = primary.Height,
            ScalePercent = primary.ScalePercent,
            RefreshHz = primary.RefreshHz
        };
    }

    public async Task StopProjectionAsync()
    {
        if (ScreenExtend.IsActive)
            await ScreenExtend.StopAsync("stopped").ConfigureAwait(false);
        // ReleaseHostDisplay 内：释放 VDD → /internal 收回窗口 → 恢复主屏快照
        await ScreenExtendUi.ReleaseHostDisplayAsync().ConfigureAwait(false);
        PublishStatus("投影：已停止（已切回仅电脑屏幕）");
    }

    public async Task<(bool Ok, string Message)> RepairProjectionEnvironmentAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 全局会话：弹窗关闭不中断；重复调用附着同一任务
        var result = await FlyScreen.ScreenExtend.MttVddRepairSession
            .RunOrAttachAsync(progress, ct).ConfigureAwait(false);
        SyncMttInstallFlag();
        return result;
    }

    public bool IsProjectionEnvironmentRepairing
        => FlyScreen.ScreenExtend.MttVddRepairSession.IsRunning;

    public string ProjectionEnvironmentRepairStatus
        => FlyScreen.ScreenExtend.MttVddRepairSession.Status;

    public async Task<(bool Ok, string Message)> UninstallProjectionEnvironmentAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (ScreenExtend.IsActive)
            await StopProjectionAsync().ConfigureAwait(false);
        var result = await FlyScreen.ScreenExtend.MttVddInstaller.TryUninstallAsync(progress, ct)
            .ConfigureAwait(false);
        SyncMttInstallFlag();
        return result;
    }

    public bool IsMttInstalledByApp
    {
        get
        {
            SyncMttInstallFlag();
            return FlyScreen.ScreenExtend.MttVddInstallRecord.IsInstalledByApp;
        }
    }

    private void SyncMttInstallFlag()
    {
        var byApp = FlyScreen.ScreenExtend.MttVddInstallRecord.IsInstalledByApp;
        if (Settings.MttVddInstalledByApp == byApp) return;
        Settings.MttVddInstalledByApp = byApp;
        Settings.Save();
    }

    private void OnPeerMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.MouseMove:
            {
                // EdgeMouse 须 SwitchFocus；AbsPointer（手机触控板）可自动进入被控
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseMovePayload>(msg.Payload);
                if (p == null) return;
                // 键鼠注入优先，不在此路径做剪贴板/磁盘 I/O（避免卡鼠标）
                if (p.Relative) InputInjector.MoveRelative(p.Dx, p.Dy);
                else InputInjector.MoveAbsoluteNormalized(p.Nx, p.Ny);
                break;
            }
            case MessageType.MouseButton:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p == null) return;
                InputInjector.MouseButton(p);
                break;
            }
            case MessageType.MouseWheel:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p == null) return;
                InputInjector.MouseWheel(p);
                break;
            }
            case MessageType.KeyEvent:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<KeyEventPayload>(msg.Payload);
                if (p == null) return;
                if (!string.IsNullOrEmpty(p.Text))
                    InputInjector.InsertText(p.Text);
                else
                    InputInjector.Key(p);
                break;
            }
            case MessageType.SwitchFocus:
            {
                var p = MessageCodec.DecodeJson<SwitchFocusPayload>(msg.Payload);
                if (p == null) return;
                if (IsLocalMachineName(p.TargetMachine))
                {
                    // 对端鼠标进入本机：显示光标；剪贴板占位由 ControlTargetChanged 触发
                    Input.BeginReceivingRemoteControl(p.EntryX, p.EntryY);
                }
                break;
            }
            case MessageType.CursorOwner:
            {
                var p = MessageCodec.DecodeJson<CursorOwnerPayload>(msg.Payload);
                if (p == null) return;
                Input.OnCursorOwnerChanged(p.OwnerMachine);
                break;
            }
            case MessageType.CompositionSelect:
            {
                var p = MessageCodec.DecodeJson<CompositionSelectPayload>(msg.Payload);
                Input.HandleCompositionSelect(peer, p);
                break;
            }
            case MessageType.CompositionCancel:
            {
                // 手机点空白 / 切应用 → 取消本机组字
                Input.HandleCompositionCancel(peer);
                break;
            }
            // LayoutSync 由 LayoutSyncHelper 处理
        }
    }

    /// <summary>
    /// EdgeMouse 依赖 SwitchFocus；手机 AbsPointer 可能未发 SwitchFocus，
    /// 在本机未控远端时自动进入被控，避免触控板键鼠被静默丢弃。
    /// </summary>
    private bool EnsureAcceptsRemoteInput(string peer)
    {
        if (Input.IsReceivingRemoteControl) return true;
        if (Input.IsControllingRemote) return false;
        if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)) return false;
        Input.BeginReceivingRemoteControl(0.5, 0.5);
        return Input.IsReceivingRemoteControl;
    }

    /// <summary>本机修改布局后权威替换广播（移除电脑）。</summary>
    public Task BroadcastLayoutSyncAsync()
    {
        var task = _layoutSync?.BroadcastReplaceAsync() ?? Task.CompletedTask;
        RestoreCursorIfLayoutSolo();
        return task;
    }

    /// <summary>添加电脑后并集广播，对端自动合并集群（最多 4 台）。</summary>
    public Task BroadcastLayoutMergeAsync()
        => _layoutSync?.BroadcastMergeAsync() ?? Task.CompletedTask;

    /// <summary>本机修改高级「功能 / 行为」后广播给已连接对端。</summary>
    public Task BroadcastBehaviorSyncAsync()
        => _behaviorSync?.BroadcastAsync() ?? Task.CompletedTask;

    /// <summary>把当前安全密钥同步到布局内设备（TCP 已连接 + UDP 密钥不同未连上）。</summary>
    public Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default)
        => _securityKeySync?.SyncCurrentKeyAsync(ct)
           ?? Task.FromResult((false, "安全密钥同步未初始化"));

    /// <summary>查询对端主屏分辨率/缩放（投影弹窗默认值）。</summary>
    public Task<DisplayInfoPayload?> QueryPeerDisplayAsync(string peer, CancellationToken ct = default)
        => _displayInfo?.QueryPeerAsync(peer, ct: ct) ?? Task.FromResult<DisplayInfoPayload?>(null);

    public DisplayInfoPayload GetLocalDisplayInfo()
        => FlyScreen.ScreenExtend.DisplayModeHelper.ToDisplayInfoPayload();

    /// <summary>布局仅剩本机时恢复可见光标（移除对端 / 同步后调用）。</summary>
    public void RestoreCursorIfLayoutSolo()
    {
        if (LayoutSlotMerge.IsOnlyLocal(Settings.MachineSlots, LocalPeerIdentity.MachineName))
            Input.RestoreCursorWhenLeavingCluster();
    }

    private static bool IsLocalMachineName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _layoutSync?.Detach();
        _layoutSync = null;
        _behaviorSync?.Detach();
        _behaviorSync = null;
        _securityKeySync?.Dispose();
        _securityKeySync = null;
        _displayInfo?.Dispose();
        _displayInfo = null;
        ScreenExtendUi.Dispose();
        ScreenExtend.Dispose();
        Clipboard.Dispose();
        Input.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
    }
}
