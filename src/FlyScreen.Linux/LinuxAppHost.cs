using FlyScreen.Linux.Environment;
using FlyScreen.Linux.Input;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

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

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;
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
        // GUI 启动后可 SetClipboardSink；CLI 默认空实现（直传仍落 Inbox）
        Transfers = new FileTransferService(Settings, Connections, NullClipboardApplySink.Instance);
        Transfers.StatusMessage += msg => StatusMessage?.Invoke(msg);
        LayoutSync = new LayoutSyncHelper(Settings, Connections);
        LayoutSync.StatusMessage += msg => StatusMessage?.Invoke(msg);
        LayoutSync.LayoutSyncedFromPeer += peer =>
        {
            LayoutSyncedFromPeer?.Invoke(peer);
            _ = RestartNetworkingAsync();
        };
        Connections.StatusMessage += msg => StatusMessage?.Invoke(msg);
        Connections.MessageReceived += OnPeerMessage;
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        RefreshCaps();
        await Connections.StartAsync().ConfigureAwait(false);
        StatusMessage?.Invoke(
            $"网络已启动 | {LinuxRuntimeEnvironment.Describe()} | 注入={Injector.BackendName}({(Injector.IsAvailable ? "可用" : "不可用")})");
    }

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
    }

    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastAsync();

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
                var p = MessageCodec.DecodeJson<MouseMovePayload>(msg.Payload);
                if (p == null) return;
                if (!_receivingRemote)
                    SetReceivingRemote(true);
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
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p != null) Injector.MouseButton(p);
                break;
            }
            case MessageType.MouseWheel:
            {
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p != null) Injector.MouseWheel(p);
                break;
            }
            case MessageType.KeyEvent:
            {
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
        if (Injector is IDisposable d)
            d.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
