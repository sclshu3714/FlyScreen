using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// MAUI 跨平台业务宿主：设置 / 连接 / 发现 / 布局同步。
/// 平台键鼠注入由后续 Adapter 接入；本阶段保证 UI 与协议路径可用。
/// </summary>
public sealed class MauiAppHost : IAsyncDisposable
{
    private static MauiAppHost? _instance;
    private int _started;

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

    public event Action<string>? StatusMessage;
    public event Action<string>? LayoutSyncedFromPeer;

    public string PlatformBanner { get; }

    private MauiAppHost()
    {
        var (platform, deviceClass, caps) = DetectIdentity();
        LocalPeerIdentity.Configure(platform: platform, deviceClass: deviceClass, caps: caps);
        PlatformBanner = $"{platform} · {deviceClass}";

        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        LayoutSync = new LayoutSyncHelper(Settings, Connections);
        LayoutSync.StatusMessage += msg => StatusMessage?.Invoke(msg);
        LayoutSync.LayoutSyncedFromPeer += peer =>
        {
            LayoutSyncedFromPeer?.Invoke(peer);
            _ = RestartNetworkingAsync();
        };
        Connections.StatusMessage += msg => StatusMessage?.Invoke(msg);
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

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        await Connections.StartAsync().ConfigureAwait(false);
        StatusMessage?.Invoke($"网络已启动 · {PlatformBanner}");
    }

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
    }

    public Task BroadcastLayoutSyncAsync() => LayoutSync.BroadcastAsync();

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
        bool sameSubnet)
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
        Settings.SameSubnetOnly = sameSubnet;
        Settings.EnsureLocalMachineSlot();
        Settings.Save();
    }

    public async ValueTask DisposeAsync()
    {
        LayoutSync.Detach();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
