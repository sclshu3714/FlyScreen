using FlyScreen.Mac.Input;
using FlyScreen.Mac.Permissions;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

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

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;

    private MacAppHost()
    {
        LocalPeerIdentity.Configure(
            platform: PeerPlatform.MacOS,
            deviceClass: PeerDeviceClass.Desktop,
            caps: PeerCaps.MacDesktopW1);

        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        Transfers = new FileTransferService(Settings, Connections, NullClipboardApplySink.Instance);
        Transfers.StatusMessage += msg => StatusMessage?.Invoke(msg);
        LayoutSync = new LayoutSyncHelper(Settings, Connections);
        LayoutSync.StatusMessage += msg => StatusMessage?.Invoke(msg);
        LayoutSync.LayoutSyncedFromPeer += peer =>
        {
            LayoutSyncedFromPeer?.Invoke(peer);
            _ = RestartNetworkingAsync();
        };
        Injector = new MacInputInjector();
        Capture = new MacInputCapture();

        Connections.StatusMessage += msg => StatusMessage?.Invoke(msg);
        Connections.MessageReceived += OnPeerMessage;
        Capture.StatusChanged += msg => StatusMessage?.Invoke(msg);
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        RefreshCapsForPermission();
        await Connections.StartAsync().ConfigureAwait(false);
        StatusMessage?.Invoke(AccessibilityPermission.IsTrusted()
            ? "网络已启动；辅助功能已授权。"
            : "网络已启动；辅助功能未授权 — 可连接，但键鼠注入/捕获不可用。");
    }

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
    }

    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastAsync();

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
                var p = MessageCodec.DecodeJson<MouseMovePayload>(msg.Payload);
                if (p == null) return;
                if (p.Relative) Injector.MoveRelative(p.Dx, p.Dy);
                else Injector.MoveAbsoluteNormalized(p.Nx, p.Ny);
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
                if (string.Equals(p.TargetMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    Injector.MoveAbsoluteNormalized(p.EntryX, p.EntryY);
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        LayoutSync.Detach();
        Capture.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
