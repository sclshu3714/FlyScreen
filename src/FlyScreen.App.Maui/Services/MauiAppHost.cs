using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;

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
    public FileTransferService Transfers { get; }
    public IMauiPlatformServices Platform => _platform;

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? BehaviorSyncedFromPeer;

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
            _ = RestartNetworkingAsync();
        };
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

    /// <summary>由 MauiProgram / 平台启动代码注入 Windows/Android 实现。</summary>
    public void AttachPlatform(IMauiPlatformServices platform)
    {
        _platform.StopDesktopInput();
        _platform.StopClipboardMonitor();
        _platform.DisposeTray();
        _platform = platform ?? NullMauiPlatformServices.Instance;
        Transfers.SetClipboardSink(_platform.ClipboardSink);
        _platform.ControlTargetChanged += peer =>
        {
            if (peer == null)
                _platform.ArmRemoteClipboardForPaste();
        };
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

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
#if ANDROID
        Platforms.Android.AndroidNetworkBootstrap.EnsureMulticastLock();
#endif
        Settings.EnsureLocalMachineSlot();
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

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
        _platform.SetEasyMouse(Settings.EasyMouse);
    }

    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastReplaceAsync();

    public Task BroadcastLayoutMergeAsync() => LayoutSync.BroadcastMergeAsync();

    public Task BroadcastBehaviorSyncAsync() => BehaviorSync.BroadcastAsync();

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
        Settings.Save();

        if (_platform.SupportsAutoStart)
            _platform.SetAutoStartEnabled(autoStart);
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
                _platform.InjectKey(p.VirtualKey, p.Down, p.Extended);
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
        if (!_platform.SupportsDesktopInput) return false;
        if (!_platform.IsFocusOnLocalMachine) return false;
        if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)) return false;
        _platform.BeginReceivingRemoteControl(0.5, 0.5);
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
        _platform.StopClipboardMonitor();
        _platform.StopDesktopInput();
        _platform.DisposeTray();
        LayoutSync.Detach();
        BehaviorSync.Detach();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
