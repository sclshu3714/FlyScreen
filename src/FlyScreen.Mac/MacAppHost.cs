using FlyScreen.Mac.Audio;
using FlyScreen.Mac.Input;
using FlyScreen.Mac.Native;
using FlyScreen.Mac.Permissions;
using FlyScreen.Mac.ScreenExtend;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Mac;

/// <summary>
/// macOS 侧组装：设置、连接、发现、被控注入。不依赖 Avalonia，便于测试。
/// </summary>
public sealed class MacAppHost : IAsyncDisposable
{
    private static MacAppHost? _instance;
    private int _started;

    public static MacAppHost Instance => _instance ?? throw new InvalidOperationException("MacAppHost 未创建");

    public static MacAppHost EnsureCreated()
    {
        if (_instance != null) return _instance;
        _instance = new MacAppHost();
        return _instance;
    }

    public AppSettings Settings { get; }
    public ConnectionManager Connections { get; }
    public FileTransferService Transfers { get; }
    public MacInputInjector Injector { get; }
    public MacInputCapture Capture { get; }
    /// <summary>在 Windows 上跑 Mac 程序时的穿屏钩子。真机 macOS 仍走 <see cref="Capture"/>。</summary>
    public WindowsEdgeInputRouter? WindowsEdge { get; private set; }
    public LayoutSyncHelper LayoutSync { get; }
    public BehaviorSyncHelper BehaviorSync { get; }
    public RemoteBindingSyncHelper RemoteBindingSync { get; }
    public SecurityKeySyncHelper SecurityKeySync { get; }
    public DisplayInfoService DisplayInfo { get; }
    public ScreenExtendService ScreenExtend { get; }
    public FlyScreen.Mac.ScreenExtend.MacProjectionHost Projection { get; }
    private readonly ProjectionAudioBridge _projectionAudio;
    public EdgeFileDragService EdgeDrag { get; }
    public bool CanFollowEdgeFileDrag => AccessibilityPermission.IsMacOS && AccessibilityPermission.IsTrusted();

    private FlyScreen.ToolSdk.ISharedDevicesRuntime? _sharedDevices;

    public void OfferEdgeFileDrag(IReadOnlyList<string> paths, int exitSide)
        => EdgeDrag.OfferFromEdge(paths, exitSide, CanFollowEdgeFileDrag ? new MacEdgeDragPointer(Injector) : null);

    public string EdgeDropLabel => "桌面";

    public bool TryGetPointerPixels(out int x, out int y)
    {
        x = y = 0;
        if (!OperatingSystem.IsMacOS()) return false;
        var pointer = new MacEdgeDragPointer(Injector);
        if (!pointer.TryRead(out var px, out var py, out _)) return false;
        var scale = 1.0;
        try
        {
            var id = CgEventNative.CGMainDisplayID();
            var bounds = CgEventNative.CGDisplayBounds(id);
            var pixels = CgEventNative.CGDisplayPixelsWide(id);
            if (bounds.Size.Width > 1)
                scale = pixels / bounds.Size.Width;
        }
        catch
        {
            scale = 1;
        }

        x = (int)Math.Round(px * scale);
        y = (int)Math.Round(py * scale);
        return true;
    }

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? BehaviorSyncedFromPeer;
    public event Action<string>? RemoteBindingSyncedFromPeer;
    public event Action<string>? SecurityKeySyncedFromPeer;

    private bool _receivingRemote;

    private MacAppHost()
    {
        LocalPeerIdentity.Configure(
            platform: PeerPlatform.MacOS,
            deviceClass: PeerDeviceClass.Desktop,
            caps: PeerCaps.MacDesktopW1);

        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        ProgramShareService.Attach(Connections);
        FlyScreen.Services.Tools.ToolService.Instance.AttachSharedDevices(Connections, Settings);
        TryWireSharedDevices();
        FlyScreen.Services.Tools.ToolService.Instance.Host.ModulesChanged += TryWireSharedDevices;
        Transfers = new FileTransferService(Settings, Connections, NullClipboardApplySink.Instance);
        Transfers.StatusMessage += PublishStatus;
        EdgeDrag = new EdgeFileDragService(Settings, Connections, Transfers, DesktopDirectory);
        EdgeDrag.StatusMessage += PublishStatus;
        LayoutSync = new LayoutSyncHelper(Settings, Connections);
        LayoutSync.StatusMessage += PublishStatus;
        LayoutSync.LayoutSyncedFromPeer += peer =>
        {
            LayoutSyncedFromPeer?.Invoke(peer);
        };
        LayoutSync.KickedFromLayout += () =>
        {
            LayoutSyncedFromPeer?.Invoke(LocalPeerIdentity.MachineName);
        };
        BehaviorSync = new BehaviorSyncHelper(Settings, Connections);
        BehaviorSync.StatusMessage += PublishStatus;
        BehaviorSync.BehaviorSyncedFromPeer += peer =>
        {
            BehaviorSyncedFromPeer?.Invoke(peer);
        };
        RemoteBindingSync = new RemoteBindingSyncHelper(Settings, Connections);
        RemoteBindingSync.StatusMessage += PublishStatus;
        RemoteBindingSync.RemoteBindingSyncedFromPeer += peer =>
        {
            try { RemoteBindingSyncedFromPeer?.Invoke(peer); } catch { /* ignore */ }
        };
        SecurityKeySync = new SecurityKeySyncHelper(Settings, Connections, Connections.Discovery);
        SecurityKeySync.StatusMessage += PublishStatus;
        SecurityKeySync.SecurityKeyAppliedFromPeer += (from, _key) =>
        {
            try { SecurityKeySyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
            _ = RestartNetworkingAsync();
        };
        DisplayInfo = new DisplayInfoService(Connections)
        {
            GetLocalDisplayInfo = () => FlyScreen.Mac.ScreenExtend.MacDisplays.ReadPrimaryPayload()
        };
        Injector = new MacInputInjector();
        Capture = new MacInputCapture();
        ScreenExtend = new ScreenExtendService(Connections);
        Projection = new FlyScreen.Mac.ScreenExtend.MacProjectionHost(ScreenExtend, Injector);
        _projectionAudio = new ProjectionAudioBridge(ScreenExtend, new MacProjectionAudio());
        EdgeDrag.RejectPeer = peer => ScreenExtend.IsExcludedFromEdgeCross(peer);
        if (OperatingSystem.IsWindows())
        {
            WindowsEdge = new WindowsEdgeInputRouter(Settings, Connections)
            {
                EdgeFileDrag = EdgeDrag,
                EasyMouseEnabled = Settings.EasyMouse,
                IsEdgeCrossExcluded = peer => ScreenExtend.IsExcludedFromEdgeCross(peer),
                ShouldPauseEasyMouseForExtend = () => ScreenExtend.ShouldPauseLocalEasyMouse
            };
        }
        ScreenExtend.StatusMessage += PublishStatus;
        ScreenExtend.ShouldAcceptOffer = (_, _) => true;
        ScreenExtend.OnHostRequestAsync = HandleRemoteHostRequestAsync;
        ScreenExtend.SessionEnded += () =>
        {
            try
            {
                if (RemoteBindingHelper.ClearAllProjecting(Settings))
                {
                    Settings.Save();
                    _ = BroadcastRemoteBindingSyncAsync();
                }
            }
            catch { /* ignore */ }
        };
        ScreenExtend.GetSinkDisplayInfo = () =>
        {
            var d = DisplayInfo.GetLocalDisplayInfo?.Invoke();
            return d == null ? (1920, 1080, 100) : (d.Width, d.Height, d.ScalePercent);
        };

        Connections.StatusMessage += PublishStatus;
        Connections.MessageReceived += OnPeerMessage;
        Capture.StatusChanged += PublishStatus;
    }

    private void TryWireSharedDevices()
    {
        var sd = FlyScreen.Services.Tools.ToolService.Instance.GetToolService<FlyScreen.ToolSdk.ISharedDevicesRuntime>();
        if (sd == null)
        {
            if (_sharedDevices != null)
                _sharedDevices.StatusMessage -= PublishStatus;
            _sharedDevices = null;
            return;
        }

        if (!ReferenceEquals(sd, _sharedDevices))
        {
            if (_sharedDevices != null)
                _sharedDevices.StatusMessage -= PublishStatus;
            _sharedDevices = sd;
            sd.StatusMessage += PublishStatus;
        }

        sd.AttachHardware(FlyScreen.ToolSdk.DefaultSharedDevicesHardware.Instance);
    }

    private void PublishStatus(string message)
    {
        AppLog.RecordStatus(message);
        StatusMessage?.Invoke(AppLog.ForStatusBar(message));
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        RefreshCapsForPermission();
        // 钩子必须装在带消息泵的 UI 线程上，且要在 ConfigureAwait(false) 之前。
        if (WindowsEdge != null)
        {
            var ok = WindowsEdge.Start();
            PublishStatus(ok
                ? "穿屏钩子已安装。"
                : "穿屏钩子安装失败，边缘切换不可用。");
        }

        await Connections.StartAsync().ConfigureAwait(false);
        PublishStatus(AccessibilityPermission.IsTrusted()
            ? "网络已启动；辅助功能已授权。"
            : "网络已启动；辅助功能未授权 — 可连接，但键鼠注入/捕获不可用。");
    }

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
    }

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
            || !string.Equals(previousSecurityKey ?? "", Settings.SecurityKey ?? "", StringComparison.Ordinal)
            || Connections.NeedsListenRebind();

        if (needRestart || !Connections.IsRunning)
            await Connections.StartAsync().ConfigureAwait(false);
        else
            await Connections.RefreshConfiguredPeersAsync().ConfigureAwait(false);
    }

    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastReplaceAsync();

    public Task BroadcastLayoutMergeAsync() => LayoutSync.BroadcastMergeAsync();

    public Task BroadcastBehaviorSyncAsync() => BehaviorSync.BroadcastAsync();

    public Task BroadcastRemoteBindingSyncAsync(string? statusHint = null)
        => RemoteBindingSync.BroadcastAsync(statusHint);

    public Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default)
        => SecurityKeySync.SyncCurrentKeyAsync(ct);

    public void RefreshCapsForPermission()
    {
        var caps = PeerCaps.MacDesktopW1.ToList();
        // Windows 上用本机钩子穿屏，不能因为没有 macOS 辅助功能授权就把 EdgeMouse 拿掉。
        if (!OperatingSystem.IsWindows() && !AccessibilityPermission.IsTrusted())
        {
            caps.Remove(PeerCaps.InjectInput);
            caps.Remove(PeerCaps.CaptureInput);
            caps.Remove(PeerCaps.EdgeMouse);
        }

        LocalPeerIdentity.Configure(caps: caps.ToArray());
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
                if (WindowsEdge != null)
                {
                    if (p.Relative) WindowsEdge.MoveRelative(p.Dx, p.Dy);
                    else WindowsEdge.MoveAbsoluteNormalized(p.Nx, p.Ny);
                }
                else if (p.Relative) Injector.MoveRelative(p.Dx, p.Dy);
                else Injector.MoveAbsoluteNormalized(p.Nx, p.Ny);
                break;
            }
            case MessageType.MouseButton:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p == null) return;
                if (WindowsEdge != null) WindowsEdge.MouseButton(p);
                else Injector.MouseButton(p);
                break;
            }
            case MessageType.MouseWheel:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p == null) return;
                if (WindowsEdge != null) WindowsEdge.MouseWheel(p);
                else Injector.MouseWheel(p);
                break;
            }
            case MessageType.KeyEvent:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<KeyEventPayload>(msg.Payload);
                if (p == null) return;
                if (WindowsEdge != null) WindowsEdge.Key(p);
                else Injector.Key(p);
                break;
            }
            case MessageType.SwitchFocus:
            {
                var p = MessageCodec.DecodeJson<SwitchFocusPayload>(msg.Payload);
                if (p == null) return;
                if (!IsLocalTarget(p.TargetMachine)) return;
                if (WindowsEdge != null)
                    WindowsEdge.BeginReceivingRemoteControl(p.EntryX, p.EntryY);
                else
                {
                    _receivingRemote = true;
                    Injector.MoveAbsoluteNormalized(p.EntryX, p.EntryY);
                }
                break;
            }
            case MessageType.CursorOwner:
            {
                var p = MessageCodec.DecodeJson<CursorOwnerPayload>(msg.Payload);
                if (p == null) return;
                if (WindowsEdge != null)
                    WindowsEdge.OnCursorOwnerChanged(p.OwnerMachine);
                else
                    _receivingRemote = IsLocalTarget(p.OwnerMachine);
                break;
            }
        }
    }

    /// <summary>
    /// 与 Win/MAUI/Linux 对齐：EdgeMouse 靠 SwitchFocus；AbsPointer 可自动进入被控。
    /// </summary>
    private bool EnsureAcceptsRemoteInput(string peer)
    {
        if (WindowsEdge != null)
        {
            if (WindowsEdge.IsReceivingRemoteControl) return true;
            if (WindowsEdge.IsControllingRemote) return false;
            if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)) return false;
            WindowsEdge.BeginReceivingRemoteControl(0.5, 0.5);
            return WindowsEdge.IsReceivingRemoteControl;
        }

        if (_receivingRemote) return true;
        if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)) return false;
        _receivingRemote = true;
        return true;
    }

    private static string DesktopDirectory()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desk = Path.Combine(home, "Desktop");
        return Directory.Exists(desk) ? desk : home;
    }

    private static bool IsLocalTarget(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>远程影子投影：对端请本机 Host 推流。</summary>
    private async Task<(bool Ok, string? Error)> HandleRemoteHostRequestAsync(
        string fromPeer,
        ScreenExtendHostRequestPayload req,
        CancellationToken ct)
    {
        if (!AccessibilityPermission.IsMacOS)
            return (false, "非 macOS，不能作为远程投影主机");
        if (!MacScreenRecording.IsGranted())
        {
            MacScreenRecording.Request();
            return (false, "需要屏幕录制权限");
        }

        var sink = string.IsNullOrWhiteSpace(req.SinkPeer) ? fromPeer : req.SinkPeer.Trim();
        if (string.IsNullOrWhiteSpace(sink))
            return (false, "未指定接收端");
        var sinkIsLocal = string.Equals(sink, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
        if (!sinkIsLocal && !Connections.IsPeerReachable(sink))
        {
            try { await Connections.EnsureAdHocPeerAsync(sink, ct).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        if (!sinkIsLocal && !Connections.IsPeerReachable(sink))
            return (false, $"接收端「{sink}」未连接");

        var d = DisplayInfo.GetLocalDisplayInfo?.Invoke();
        var choice = new ProjectionDisplayChoice
        {
            Source = ProjectionDisplaySource.HostPrimary,
            Width = req.PreferredWidth > 0 ? req.PreferredWidth : (d?.Width ?? 1920),
            Height = req.PreferredHeight > 0 ? req.PreferredHeight : (d?.Height ?? 1080),
            ScalePercent = d?.ScalePercent > 0 ? d.ScalePercent : 100,
            Fps = req.Fps > 0 ? req.Fps : ProjectionDisplayPresets.DefaultFps,
            JpegQuality = req.JpegQuality > 0
                ? ProjectionDisplayPresets.ClampJpegQuality(req.JpegQuality)
                : ProjectionDisplayPresets.DefaultJpegQuality
        };
        Projection.Configure(DisplayProjectionMode.Clone, choice, LayoutEdgeSide.Unknown);
        Projection.ReleaseExtend();

        if (ScreenExtend.IsActive)
        {
            var same = ScreenExtend.Role == ScreenExtendRole.Host
                       && string.Equals(ScreenExtend.Peer, sink, StringComparison.OrdinalIgnoreCase);
            if (!same)
                return (false, ScreenExtendConflict.DescribeLocalBusy(ScreenExtend.Role, ScreenExtend.Peer));
            Projection.Configure(DisplayProjectionMode.Clone, choice, LayoutEdgeSide.Unknown);
            try { ScreenExtend.NotifyHostSessionReady(); } catch { /* ignore */ }
            return (true, null);
        }

        return await ScreenExtend.StartHostAsync(
                sink,
                choice.Width,
                choice.Height,
                fps: choice.Fps > 0 ? choice.Fps : 12,
                audioSync: req.AudioSync,
                remoteControl: req.RemoteControl,
                ct: ct)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { ProgramService.Instance.Runner.StopAllForShutdown(); }
        catch { /* ignore */ }
        LayoutSync.Detach();
        BehaviorSync.Detach();
        RemoteBindingSync.Detach();
        SecurityKeySync.Dispose();
        DisplayInfo.Dispose();
        _projectionAudio.Dispose();
        Projection.Dispose();
        ScreenExtend.Dispose();
        EdgeDrag.Dispose();
        WindowsEdge?.Dispose();
        Capture.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
