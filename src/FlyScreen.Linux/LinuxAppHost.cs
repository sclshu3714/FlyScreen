using FlyScreen.Linux.Environment;
using FlyScreen.Linux.Input;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Linux;

/// <summary>
/// Linux 业务宿主：GUI / CLI / Daemon 共用。不依赖 Avalonia。
/// </summary>
public sealed class LinuxAppHost : IAsyncDisposable
{
    private static LinuxAppHost? _instance;
    private int _started;

    public static LinuxAppHost Instance => _instance ?? throw new InvalidOperationException("LinuxAppHost 未创建");

    public static LinuxAppHost EnsureCreated()
    {
        if (_instance != null) return _instance;
        _instance = new LinuxAppHost();
        return _instance;
    }

    public AppSettings Settings { get; }
    public ConnectionManager Connections { get; }
    public FileTransferService Transfers { get; }
    public LinuxSessionKind SessionKind { get; }
    public ILinuxInputInjector Injector { get; private set; }
    public LayoutSyncHelper LayoutSync { get; }
    public BehaviorSyncHelper BehaviorSync { get; }
    public SecurityKeySyncHelper SecurityKeySync { get; }
    public DisplayInfoService DisplayInfo { get; }
    public ScreenExtendService ScreenExtend { get; }
    public EdgeFileDragService EdgeDrag { get; }
    public bool CanFollowEdgeFileDrag => Injector is X11InputInjector x11 && x11.IsAvailable;

    public void OfferEdgeFileDrag(IReadOnlyList<string> paths, int exitSide)
    {
        IEdgeDragPointer? follow = Injector is X11InputInjector x11 && x11.IsAvailable
            ? new X11EdgeDragPointer(x11)
            : null;
        EdgeDrag.OfferFromEdge(paths, exitSide, follow);
    }

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? BehaviorSyncedFromPeer;
    public event Action<string>? SecurityKeySyncedFromPeer;
    /// <summary>被远程控制时指针归一化坐标更新（供 GUI 软件光标）。</summary>
    public event Action<double, double>? RemotePointerMoved;
    /// <summary>是否处于「被对端操控本机」状态。</summary>
    public event Action<bool>? ReceivingRemoteControlChanged;

    private bool _receivingRemote;

    private LinuxAppHost()
    {
        SessionKind = LinuxRuntimeEnvironment.DetectSessionKind();
        Injector = LinuxInputFactory.Create(SessionKind);

        LocalPeerIdentity.Configure(
            platform: PeerPlatform.Linux,
            deviceClass: SessionKind == LinuxSessionKind.Headless
                ? PeerDeviceClass.Unknown
                : PeerDeviceClass.Desktop,
            caps: LinuxInputFactory.CapsFor(SessionKind, Injector));

        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        ProgramShareService.Attach(Connections);
        // GUI 启动后可 SetClipboardSink；CLI 默认空实现（直传仍落 Inbox）
        Transfers = new FileTransferService(Settings, Connections, NullClipboardApplySink.Instance);
        Transfers.StatusMessage += PublishStatus;
        EdgeDrag = new EdgeFileDragService(Settings, Connections, Transfers, DesktopDirectory);
        EdgeDrag.StatusMessage += PublishStatus;
        LayoutSync = new LayoutSyncHelper(Settings, Connections);
        LayoutSync.StatusMessage += PublishStatus;
        LayoutSync.LayoutSyncedFromPeer += peer =>
        {
            if (LayoutSlotMerge.IsOnlyLocal(Settings.MachineSlots, LocalPeerIdentity.MachineName))
                Injector.EnsureCursorVisible();
            LayoutSyncedFromPeer?.Invoke(peer);
        };
        LayoutSync.KickedFromLayout += () =>
        {
            Injector.EnsureCursorVisible();
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
        ScreenExtend = new ScreenExtendService(Connections);
        EdgeDrag.RejectPeer = peer => ScreenExtend.IsExcludedFromEdgeCross(peer);
        ScreenExtend.GetSinkDisplayInfo = () =>
        {
            var d = DisplayInfo.GetLocalDisplayInfo?.Invoke();
            return d == null ? (1920, 1080, 100) : (d.Width, d.Height, d.ScalePercent);
        };
        ScreenExtend.StatusMessage += PublishStatus;
        ScreenExtend.ShouldAcceptOffer = (_, _) => true;
        Connections.StatusMessage += PublishStatus;
        Connections.MessageReceived += OnPeerMessage;
    }

    private void PublishStatus(string message)
    {
        AppLog.RecordStatus(message);
        StatusMessage?.Invoke(AppLog.ForStatusBar(message));
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        RefreshCaps();
        await Connections.StartAsync().ConfigureAwait(false);
        PublishStatus(
            $"网络已启动 | {LinuxRuntimeEnvironment.Describe()} | 注入={Injector.BackendName}({(Injector.IsAvailable ? "可用" : "不可用")})");
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

    public void RefreshCaps()
    {
        if (SessionKind == LinuxSessionKind.X11 && !Injector.IsAvailable)
        {
            if (Injector is IDisposable old)
                old.Dispose();
            Injector = LinuxInputFactory.Create(SessionKind);
        }

        LocalPeerIdentity.Configure(caps: LinuxInputFactory.CapsFor(SessionKind, Injector));
    }

    public void ApplySettingsFrom(string securityKey, IReadOnlyList<string> slots)
    {
        Settings.SecurityKey = securityKey.Trim();
        Settings.MachineSlots = slots.Select(s => s.Trim()).ToList();
        while (Settings.MachineSlots.Count < 4)
            Settings.MachineSlots.Add("");
        Settings.EnsureLocalMachineSlot();
        Settings.Save();
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
                if (p.Relative)
                {
                    if (Injector.TryGetPointerNormalized(out var nx, out var ny))
                        RemotePointerMoved?.Invoke(nx, ny);
                }
                else
                {
                    RemotePointerMoved?.Invoke(p.Nx, p.Ny);
                }
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
                Injector.EnsureCursorVisible();
                SetReceivingRemote(true);
                Injector.MoveAbsoluteNormalized(p.EntryX, p.EntryY);
                RemotePointerMoved?.Invoke(p.EntryX, p.EntryY);
                break;
            }
            case MessageType.CursorOwner:
            {
                var p = MessageCodec.DecodeJson<CursorOwnerPayload>(msg.Payload);
                if (p == null) return;
                if (IsLocalTarget(p.OwnerMachine))
                {
                    Injector.EnsureCursorVisible();
                    SetReceivingRemote(true);
                }
                else
                {
                    SetReceivingRemote(false);
                }
                break;
            }
        }
    }

    /// <summary>
    /// 与 Win/MAUI 对齐：EdgeMouse 靠 SwitchFocus；AbsPointer（手机触控板）可自动进入被控。
    /// </summary>
    private bool EnsureAcceptsRemoteInput(string peer)
    {
        if (_receivingRemote) return true;
        if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)) return false;
        SetReceivingRemote(true);
        return _receivingRemote;
    }

    private static string DesktopDirectory()
    {
        var dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var desk = Path.Combine(home, "Desktop");
        return Directory.Exists(desk) ? desk : home;
    }

    private static bool IsLocalTarget(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        // 兼容仍按裸 Environment.MachineName 广播的旧对端
        return string.Equals(name, System.Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private void SetReceivingRemote(bool value)
    {
        if (_receivingRemote == value) return;
        _receivingRemote = value;
        if (value) Injector.EnsureCursorVisible();
        ReceivingRemoteControlChanged?.Invoke(value);
    }

    public async ValueTask DisposeAsync()
    {
        LayoutSync.Detach();
        BehaviorSync.Detach();
        SecurityKeySync.Dispose();
        DisplayInfo.Dispose();
        ScreenExtend.Dispose();
        EdgeDrag.Dispose();
        if (Injector is IDisposable d)
            d.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
