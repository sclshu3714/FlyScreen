using System.Reflection;
using System.Runtime.InteropServices;

namespace FlyScreen.Services.Network;

/// <summary>
/// 本机在握手中宣告的平台 / 形态 / 能力。各 App 可覆盖 Caps（例如 Mac 权限未授予时去掉 CaptureInput）。
/// </summary>
public static class LocalPeerIdentity
{
    private static string[]? _capsOverride;
    private static string? _machineNameOverride;
    private static string? _resolvedMachineName;

    public static string Platform { get; private set; } = DetectPlatform();
    public static string DeviceClass { get; private set; } = PeerDeviceClass.Desktop;
    public static int ProtocolVersion { get; } = 2;

    /// <summary>
    /// 对端可见的本机名。WSL 下 Environment.MachineName 常等于 Windows 主机名，
    /// 会导致发现包被双方当作「自己」丢弃，故自动加发行版后缀。
    /// </summary>
    public static string MachineName =>
        _machineNameOverride
        ?? _resolvedMachineName
        ?? (_resolvedMachineName = ResolveMachineName());

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string[] Caps => _capsOverride ?? DefaultCapsForPlatform();

    public static void Configure(
        string? platform = null,
        string? deviceClass = null,
        string[]? caps = null,
        string? machineName = null)
    {
        if (!string.IsNullOrWhiteSpace(platform))
            Platform = platform;
        if (!string.IsNullOrWhiteSpace(deviceClass))
            DeviceClass = deviceClass;
        _capsOverride = caps;
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            _machineNameOverride = machineName.Trim();
            _resolvedMachineName = null;
        }
    }

    /// <summary>从设置覆盖本机名（空则恢复自动探测）。</summary>
    public static void SetMachineNameOverride(string? name)
    {
        _machineNameOverride = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _resolvedMachineName = null;
    }

    private static string ResolveMachineName()
    {
        var envName = Environment.MachineName?.Trim() ?? "FlyScreen";

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return envName;

        // 优先 /etc/hostname（若与 Windows 主机名不同，可直接区分）
        try
        {
            if (File.Exists("/etc/hostname"))
            {
                var host = File.ReadAllText("/etc/hostname").Trim();
                if (!string.IsNullOrWhiteSpace(host)
                    && !host.Equals(envName, StringComparison.OrdinalIgnoreCase))
                    return host;
            }
        }
        catch
        {
            // ignore
        }

        if (!IsWsl())
            return envName;

        // WSL：与宿主机同名时加发行版后缀，保证发现/握手身份唯一
        var distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")?.Trim();
        if (string.IsNullOrWhiteSpace(distro))
            distro = "WSL";
        distro = new string(distro.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(distro))
            distro = "WSL";

        var candidate = $"{envName}-{distro}";
        return candidate.Length > 63 ? $"{envName}-WSL" : candidate;
    }

    public static bool IsWsl()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
            return true;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")))
            return true;
        try
        {
            if (!File.Exists("/proc/version")) return false;
            var v = File.ReadAllText("/proc/version");
            return v.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                   || v.Contains("WSL", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
