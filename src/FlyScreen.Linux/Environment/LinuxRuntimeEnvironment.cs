using System.Runtime.InteropServices;

namespace FlyScreen.Linux.Environment;

/// <summary>
/// 探测 Linux 图形会话。兼容常见发行版：通过环境变量与 XDG_SESSION_TYPE，
/// 不依赖特定桌面（GNOME/KDE/XFCE 等）。
/// </summary>
public static class LinuxRuntimeEnvironment
{
    private const string NoneLabel = "(none)";

    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static string? Display => GetEnv("DISPLAY");
    public static string? WaylandDisplay => GetEnv("WAYLAND_DISPLAY");
    public static string? XdgSessionType => GetEnv("XDG_SESSION_TYPE");
    public static string? DesktopSession => GetEnv("XDG_CURRENT_DESKTOP") ?? GetEnv("DESKTOP_SESSION");

    public static bool IsRunningAsRoot =>
        IsLinux && geteuid() == 0;

    /// <summary>是否存在可用图形会话（可尝试启动 GUI）。</summary>
    public static bool HasGraphicalSession =>
        !string.IsNullOrWhiteSpace(Display) || !string.IsNullOrWhiteSpace(WaylandDisplay);

    /// <summary>
    /// 启动 GUI 前补齐常见图形环境（尤其 WSL2 + WSLg：sudo 会丢掉 DISPLAY）。
    /// 仅在当前未设置时探测并写回环境变量。
    /// </summary>
    public static bool TryEnsureGraphicalEnvironment(out string? hint)
    {
        hint = null;
        if (HasGraphicalSession) return true;

        // WSLg / 本机 Xorg：存在 X0 套接字时默认 :0
        if (File.Exists("/tmp/.X11-unix/X0") || Directory.Exists("/mnt/wslg"))
        {
            System.Environment.SetEnvironmentVariable("DISPLAY", ":0");
            hint = "已自动设置 DISPLAY=:0（WSLg/本地 X11）";
            return true;
        }

        for (var n = 0; n <= 3; n++)
        {
            if (!File.Exists($"/tmp/.X11-unix/X{n}")) continue;
            System.Environment.SetEnvironmentVariable("DISPLAY", $":{n}");
            hint = $"已自动设置 DISPLAY=:{n}";
            return true;
        }

        if (File.Exists("/run/user/1000/wayland-0") || File.Exists("/run/user/1000/wayland-1"))
        {
            var wayland = File.Exists("/run/user/1000/wayland-0") ? "wayland-0" : "wayland-1";
            System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
            System.Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/1000");
            hint = $"已自动设置 WAYLAND_DISPLAY={wayland}";
            return true;
        }

        return false;
    }

    public static string GuiFailureAdvice(string? exceptionMessage)
    {
        var lines = new List<string>();
        if (IsRunningAsRoot)
        {
            lines.Add("检测到 root/sudo：DISPLAY 常被清空，且 X11 cookie 不可用。");
            lines.Add("请用普通用户启动：  ./FlyScreen --gui");
            lines.Add("或保留环境：  sudo -E ./FlyScreen --gui");
            lines.Add("或显式指定：  sudo DISPLAY=:0 ./FlyScreen --gui");
        }
        else if (!HasGraphicalSession)
        {
            lines.Add("当前无 DISPLAY/WAYLAND_DISPLAY。");
            lines.Add("WSL2 + WSLg：  export DISPLAY=:0 && ./FlyScreen --gui");
            lines.Add("或启用 Windows「适用于 Linux 的 GUI 应用支持」。");
        }

        if (!string.IsNullOrWhiteSpace(exceptionMessage)
            && exceptionMessage.Contains("XOpenDisplay", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("XOpenDisplay 失败：确认 WSLg/X 服务器已运行，且本用户有权连接该 DISPLAY。");
        }

        return lines.Count == 0
            ? "请检查图形会话与依赖库后重试。"
            : string.Join(System.Environment.NewLine, lines);
    }

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
        return string.Concat(
            "Session=", kind,
            "; DISPLAY=", Display ?? NoneLabel,
            "; WAYLAND_DISPLAY=", WaylandDisplay ?? NoneLabel,
            "; XDG_SESSION_TYPE=", XdgSessionType ?? NoneLabel,
            "; Desktop=", DesktopSession ?? NoneLabel);
    }

    private static string? GetEnv(string name)
    {
        var v = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    [DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();
}
