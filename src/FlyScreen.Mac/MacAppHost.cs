using FlyScreen.Mac.Input;
using FlyScreen.Mac.Permissions;
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
    public LayoutSyncHelper LayoutSync { get; }
    public BehaviorSyncHelper BehaviorSync { get; }
    public SecurityKeySyncHelper SecurityKeySync { get; }
    public DisplayInfoService DisplayInfo { get; }
    public ScreenExtendService ScreenExtend { get; }
    public EdgeFileDragService EdgeDrag { get; }
    public bool CanFollowEdgeFileDrag => AccessibilityPermission.IsMacOS && AccessibilityPermission.IsTrusted();

    public void OfferEdgeFileDrag(IReadOnlyList<string> paths, int exitSide)
        => EdgeDrag.OfferFromEdge(paths, exitSide, CanFollowEdgeFileDrag ? new MacEdgeDragPointer(Injector) : null);

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? BehaviorSyncedFromPeer;
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
        SecurityKeySync = new SecurityKeySyncHelper(Settings, Connections, Connections.Discovery);
        SecurityKeySync.StatusMessage += PublishStatus;
        SecurityKeySync.SecurityKeyAppliedFromPeer += (from, _key) =>
        {
            try { SecurityKeySyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
            _ = RestartNetworkingAsync();
        };
        DisplayInfo = new DisplayInfoService(Connections)
        {
            GetLocalDisplayInfo = () => new DisplayInfoPayload
            {
                Width = 1920,
                Height = 1080,
                ScalePercent = 100,
                RefreshHz = 60
            }
        };
        Injector = new MacInputInjector();
        Capture = new MacInputCapture();
        ScreenExtend = new ScreenExtendService(Connections);
        EdgeDrag.RejectPeer = peer => ScreenExtend.IsExcludedFromEdgeCross(peer);
        ScreenExtend.StatusMessage += PublishStatus;
        ScreenExtend.ShouldAcceptOffer = (_, _) => true;
        ScreenExtend.GetSinkDisplayInfo = () =>
        {
            var d = DisplayInfo.GetLocalDisplayInfo?.Invoke();
            return d == null ? (1920, 1080, 100) : (d.Width, d.Height, d.ScalePercent);
        };

        Connections.StatusMessage += PublishStatus;
        Connections.MessageReceived += OnPeerMessage;
        Capture.StatusChanged += PublishStatus;
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
            || !string.Equals(previousSecurityKey ?? "", Settings.SecurityKey ?? "", StringComparison.Ordinal);

        if (needRestart || !Connections.IsRunning)
            await Connections.StartAsync().ConfigureAwait(false);
        else
            await Connections.RefreshConfiguredPeersAsync().ConfigureAwait(false);
    }

    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastReplaceAsync();

    public Task BroadcastLayoutMergeAsync() => LayoutSync.BroadcastMergeAsync();

    public Task BroadcastBehaviorSyncAsync() => BehaviorSync.BroadcastAsync();

    public Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default)
        => SecurityKeySync.SyncCurrentKeyAsync(ct);

    public void RefreshCapsForPermission()
    {
        var caps = PeerCaps.MacDesktopW1.ToList();
        if (!AccessibilityPermission.IsTrusted())
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
                if (p.Relative) Injector.MoveRelative(p.Dx, p.Dy);
                else Injector.MoveAbsoluteNormalized(p.Nx, p.Ny);
                break;
            }
            case MessageType.MouseButton:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p != null) Injector.MouseButton(p);
                break;
            }
            case MessageType.MouseWheel:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p != null) Injector.MouseWheel(p);
                break;
            }
            case MessageType.KeyEvent:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<KeyEventPayload>(msg.Payload);
                if (p != null) Injector.Key(p);
                break;
            }
            case MessageType.SwitchFocus:
            {
                var p = MessageCodec.DecodeJson<SwitchFocusPayload>(msg.Payload);
                if (p == null) return;
                if (!IsLocalTarget(p.TargetMachine)) return;
                _receivingRemote = true;
                Injector.MoveAbsoluteNormalized(p.EntryX, p.EntryY);
                break;
            }
            case MessageType.CursorOwner:
            {
                var p = MessageCodec.DecodeJson<CursorOwnerPayload>(msg.Payload);
                if (p == null) return;
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

    public async ValueTask DisposeAsync()
    {
        LayoutSync.Detach();
        BehaviorSync.Detach();
        SecurityKeySync.Dispose();
        DisplayInfo.Dispose();
        ScreenExtend.Dispose();
        EdgeDrag.Dispose();
        Capture.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
