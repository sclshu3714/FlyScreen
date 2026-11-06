using System.Collections.ObjectModel;
using FlyScreen.DesktopUi;
using FlyScreen.DesktopUi.Services;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Mac;
using FlyScreen.Mac.Permissions;
using FlyScreen.Mac.ScreenExtend;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.MacApp;

internal sealed class MacDesktopShellHost : IDesktopShellHost
{
    private readonly MacAppHost _host;
    private readonly AvaloniaClipboardApplySink _clipSink = new();
    private readonly DesktopClipboardCoordinator _clipCoord;
    private readonly ScreenExtendDesktopController _screenExtendUi;

    public MacDesktopShellHost(MacAppHost host)
    {
        _host = host;
        _host.Transfers.SetClipboardSink(_clipSink);
        _clipCoord = new DesktopClipboardCoordinator(host.Settings, host.Transfers, _clipSink);
        // 网络启动前挂好从机 UI，避免 Offer 已 Accept 却无全屏窗
        _screenExtendUi = new ScreenExtendDesktopController(host.ScreenExtend);
        _host.EdgeDrag.ConfirmConflict = EdgeDragConflictPrompt.AskAsync;
    }

    public AppSettings Settings => _host.Settings;
    public ConnectionManager Connections => _host.Connections;
    public FileTransferService Transfers => _host.Transfers;
    public ObservableCollection<TransferItem> TransferItems => _host.Transfers.Items;

    public event Action<string>? StatusMessage
    {
        add => _host.StatusMessage += value;
        remove => _host.StatusMessage -= value;
    }

    public event Action<string>? LayoutSyncedFromPeer
    {
        add => _host.LayoutSyncedFromPeer += value;
        remove => _host.LayoutSyncedFromPeer -= value;
    }

    public event Action<string>? BehaviorSyncedFromPeer
    {
        add => _host.BehaviorSyncedFromPeer += value;
        remove => _host.BehaviorSyncedFromPeer -= value;
    }

    public event Action<string>? SecurityKeySyncedFromPeer
    {
        add => _host.SecurityKeySyncedFromPeer += value;
        remove => _host.SecurityKeySyncedFromPeer -= value;
    }

    public string PlatformStatusLine
    {
        get
        {
            var trusted = AccessibilityPermission.IsTrusted();
            if (AccessibilityPermission.IsMacOS)
                return $"macOS · 辅助功能={(trusted ? "已授权" : "未授权")} · Caps={string.Join(",", LocalPeerIdentity.Caps)}";
            var hook = _host.WindowsEdge?.HookInstalled == true ? "穿屏钩子已安装" : "穿屏钩子未安装";
            return $"非 macOS 运行 · {hook} · Caps={string.Join(",", LocalPeerIdentity.Caps)}";
        }
    }

    public IReadOnlyList<(string Label, Action Invoke)> PlatformActions { get; } =
    [
        ("打开辅助功能设置", AccessibilityPermission.OpenSystemSettings),
        ("打开屏幕录制设置", MacScreenRecording.OpenSettings)
    ];

    public bool SupportsTray => true;
    public bool SupportsAutoStart => false;

    public Task StartAsync() => _host.StartAsync();
    public Task RestartNetworkingAsync() => _host.RestartNetworkingAsync();
    public Task ApplyNetworkingAsync(
        int previousTcpPort,
        int previousUdpPort,
        string previousNicId,
        string previousLocalIp,
        string previousSecurityKey)
        => _host.ApplyNetworkingAsync(previousTcpPort, previousUdpPort, previousNicId, previousLocalIp, previousSecurityKey);
    public Task BroadcastLayoutSyncAsync() => _host.BroadcastLayoutSyncAsync();
    public Task BroadcastLayoutMergeAsync() => _host.BroadcastLayoutMergeAsync();
    public Task BroadcastBehaviorSyncAsync() => _host.BroadcastBehaviorSyncAsync();
    public Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default)
        => _host.SyncSecurityKeyAsync(ct);

    public ScreenExtendService ScreenExtend => _host.ScreenExtend;

    public bool CanStartProjection => AccessibilityPermission.IsMacOS;

    public (bool Ready, string Message) DescribeProjectionEnvironment(DisplayProjectionMode mode)
    {
        if (!AccessibilityPermission.IsMacOS)
            return (false, "非 macOS，不能发起投影。");
        if (!MacScreenRecording.IsGranted())
            return (false, "需要屏幕录制权限。请到系统设置 → 隐私与安全性 → 屏幕录制，允许玄鸟后再试。");
        if (mode == DisplayProjectionMode.Extend && !MacDisplays.HasSecondary())
            return (true, "没有第二块屏幕。开始后会创建虚拟副屏，并推到所选电脑。");
        if (mode == DisplayProjectionMode.Extend)
            return (true, "将采集已连接的第二块屏幕。");
        return (true, "将采集本机主屏并推到所选电脑。");
    }

    public async Task<(bool Ok, string? Message)> StartProjectionAsync(
        ProjectionTarget? target,
        DisplayProjectionMode mode,
        bool audioSync = false,
        bool cropBlackBars = false,
        bool remoteControl = false)
    {
        if (!AccessibilityPermission.IsMacOS)
            return (false, "非 macOS，不能发起投影。");

        mode = DisplayProjectionModeInfo.ClampForPicker((int)mode);
        if (!MacScreenRecording.IsGranted())
        {
            MacScreenRecording.Request();
            MacScreenRecording.OpenSettings();
            return (false, "需要屏幕录制权限。已打开系统设置，允许玄鸟后重新开始。");
        }

        if (target == null || target.Kind != ProjectionTargetKind.Peer || string.IsNullOrWhiteSpace(target.Id))
            return (false, "请选择一台双向已连接的电脑");

        var peer = target.Id.Trim();
        if (!_host.Connections.GetPeerStatus(peer).Bidirectional)
            return (false, $"「{peer}」未双向连接，无法投影");
        if (!ScreenExtendTargets.IsEligiblePeer(_host.Connections, peer))
            return (false, $"「{peer}」不是可投影的电脑");

        var sameHostPeer = ScreenExtend.Role == ScreenExtendRole.Host
            && string.Equals(ScreenExtend.Peer, peer, StringComparison.OrdinalIgnoreCase);
        if (ScreenExtend.IsActive && !sameHostPeer)
            return (false, ScreenExtendConflict.DescribeLocalBusy(ScreenExtend.Role, ScreenExtend.Peer));

        var choice = new ProjectionDisplayChoice
        {
            Source = Enum.IsDefined(typeof(ProjectionDisplaySource), _host.Settings.ScreenExtendDisplaySource)
                ? (ProjectionDisplaySource)_host.Settings.ScreenExtendDisplaySource
                : ProjectionDisplaySource.HostPrimary,
            Width = _host.Settings.ScreenExtendWidth,
            Height = _host.Settings.ScreenExtendHeight,
            ScalePercent = ProjectionDisplayPresets.ClampScale(
                _host.Settings.ScreenExtendScalePercent > 0 ? _host.Settings.ScreenExtendScalePercent : 100),
            Fps = ProjectionDisplayPresets.RememberedFps(_host.Settings),
            JpegQuality = ProjectionDisplayPresets.RememberedJpegQuality(_host.Settings),
            CropBlackBars = cropBlackBars
        };
        if (mode == DisplayProjectionMode.Clone && choice.Source == ProjectionDisplaySource.TargetPeer)
            choice.Source = ProjectionDisplaySource.HostPrimary;

        _host.Settings.ScreenExtendProjectionMode = (int)mode;
        _host.Settings.ScreenExtendPeer = peer;
        ProjectionDisplayPresets.WriteRemembered(_host.Settings, choice, audioSync, remoteControl);
        _host.Settings.Save();

        var side = LayoutPeerDirection.Resolve(
            _host.Settings.MachineSlots,
            _host.Settings.TwoRow,
            LocalPeerIdentity.MachineName,
            peer,
            Environment.MachineName);
        _host.Projection.Configure(mode, choice, side);

        if (ScreenExtend.IsActive)
            await ScreenExtend.StopAsync("retarget").ConfigureAwait(false);

        if (mode == DisplayProjectionMode.Extend)
        {
            var (ready, detail) = _host.Projection.PrepareExtend();
            if (!ready)
                return (false, detail ?? "无法准备扩展屏幕");
        }
        else
        {
            _host.Projection.ReleaseExtend();
        }

        var (ok, err) = await ScreenExtend.StartHostAsync(
                peer,
                choice.Width > 0 ? choice.Width : 1920,
                choice.Height > 0 ? choice.Height : 1080,
                choice.Fps,
                audioSync,
                remoteControl)
            .ConfigureAwait(false);
        if (!ok)
        {
            _host.Projection.ReleaseExtend();
            return (false, err ?? "发起投影失败");
        }

        var audioNote = audioSync ? "。采集期间本机扬声器静音，停止后恢复" : "";
        return (true, $"已向 {peer} 发起{DisplayProjectionModeInfo.Label(mode)}投影{audioNote}");
    }

    public async Task StopProjectionAsync()
    {
        if (ScreenExtend.IsActive)
            await ScreenExtend.StopAsync("stopped").ConfigureAwait(false);
    }

    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }

    public bool CanFollowEdgeFileDrag => _host.CanFollowEdgeFileDrag;

    public void OfferEdgeFileDrag(IReadOnlyList<string> paths, int exitSide)
        => _host.OfferEdgeFileDrag(paths, exitSide);

    public event Action<bool>? EdgeFileDragActiveChanged
    {
        add => _host.EdgeDrag.ActiveChanged += value;
        remove => _host.EdgeDrag.ActiveChanged -= value;
    }

    public event Action<EdgeDragCue?>? EdgeDragCueChanged
    {
        add => _host.EdgeDrag.CueChanged += value;
        remove => _host.EdgeDrag.CueChanged -= value;
    }

    public bool TryGetEdgeDragPointer(out int x, out int y)
        => _host.TryGetPointerPixels(out x, out y);

    public string EdgeDragDropLabel => _host.EdgeDropLabel;
}
