using System.Reflection;
using System.Runtime.InteropServices;

namespace FlyScreen.Services.Network;

/// <summary>
/// 本机在握手中宣告的平台 / 形态 / 能力。各 App 可覆盖 Caps（例如 Mac 权限未授予时去掉 CaptureInput）。
/// </summary>
public static class LocalPeerIdentity
{
    private static string[]? _capsOverride;

    public static string Platform { get; private set; } = DetectPlatform();
    public static string DeviceClass { get; private set; } = PeerDeviceClass.Desktop;
    public static int ProtocolVersion { get; } = 2;

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string[] Caps => _capsOverride ?? DefaultCapsForPlatform();

    public static void Configure(string? platform = null, string? deviceClass = null, string[]? caps = null)
    {
        if (!string.IsNullOrWhiteSpace(platform))
            Platform = platform;
        if (!string.IsNullOrWhiteSpace(deviceClass))
            DeviceClass = deviceClass;
        _capsOverride = caps;
    }

    public static HandshakePayload Enrich(HandshakePayload payload)
    {
        payload.ProtocolVersion = ProtocolVersion;
        payload.Platform = Platform;
        payload.DeviceClass = DeviceClass;
        payload.Caps = Caps;
        payload.AppVersion = AppVersion;
        return payload;
    }

    public static string[] IntersectCaps(string[]? remote)
    {
        if (remote == null || remote.Length == 0)
            return Caps; // 旧端无 Caps：按本机能力继续，消息级降级
        return Caps.Intersect(remote, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PeerPlatform.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return PeerPlatform.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PeerPlatform.Linux;
        return "Unknown";
    }

    private static string[] DefaultCapsForPlatform()
        => Platform switch
        {
            PeerPlatform.MacOS => PeerCaps.MacDesktopW1,
            PeerPlatform.Windows => PeerCaps.DesktopFull,
            // Linux 具体 Caps 由 FlyScreen.Linux 按 X11/Wayland/无头覆盖
            PeerPlatform.Linux => PeerCaps.LinuxDesktopX11,
            _ => PeerCaps.DesktopFull
        };
}
