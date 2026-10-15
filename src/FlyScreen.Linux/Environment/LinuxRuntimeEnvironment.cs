using System.Runtime.InteropServices;

namespace FlyScreen.Linux.Environment;

/// <summary>
/// 探测 Linux 图形会话。兼容常见发行版：通过环境变量与 XDG_SESSION_TYPE，
/// 不依赖特定桌面（GNOME/KDE/XFCE 等）。
/// </summary>
public static class LinuxRuntimeEnvironment
{
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static string? Display => GetEnv("DISPLAY");
    public static string? WaylandDisplay => GetEnv("WAYLAND_DISPLAY");
    public static string? XdgSessionType => GetEnv("XDG_SESSION_TYPE");
    public static string? DesktopSession => GetEnv("XDG_CURRENT_DESKTOP") ?? GetEnv("DESKTOP_SESSION");

    /// <summary>是否存在可用图形会话（可尝试启动 GUI）。</summary>
    public static bool HasGraphicalSession =>
        !string.IsNullOrWhiteSpace(Display) || !string.IsNullOrWhiteSpace(WaylandDisplay);

    public static LinuxSessionKind DetectSessionKind()
    {
        if (!IsLinux)
        {
            // 非 Linux 开发机：便于在 Windows 上编译/跑 CLI 骨架
            return HasGraphicalSession ? LinuxSessionKind.X11 : LinuxSessionKind.Headless;
        }

        var session = (XdgSessionType ?? "").Trim().ToLowerInvariant();
        if (session == "wayland" && !string.IsNullOrWhiteSpace(WaylandDisplay))
            return LinuxSessionKind.Wayland;

        if (!string.IsNullOrWhiteSpace(Display))
            return LinuxSessionKind.X11;

        if (!string.IsNullOrWhiteSpace(WaylandDisplay))
            return LinuxSessionKind.Wayland;

        if (session is "x11" or "mir")
            return string.IsNullOrWhiteSpace(Display) ? LinuxSessionKind.Headless : LinuxSessionKind.X11;

        return LinuxSessionKind.Headless;
    }

    public static string Describe()
    {
        var kind = DetectSessionKind();
        return $"Session={kind}; DISPLAY={Display ?? "(none)"}; WAYLAND_DISPLAY={WaylandDisplay ?? "(none)"}; " +
               $"XDG_SESSION_TYPE={XdgSessionType ?? "(none)"}; Desktop={DesktopSession ?? "(none)"}";
    }

    private static string? GetEnv(string name)
    {
        var v = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
