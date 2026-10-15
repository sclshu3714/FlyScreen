namespace FlyScreen.Linux.Environment;

/// <summary>本机图形会话类型（决定 Caps 与注入实现）。</summary>
public enum LinuxSessionKind
{
    /// <summary>无 DISPLAY / WAYLAND_DISPLAY：服务器或纯 SSH。</summary>
    Headless,
    /// <summary>X11（含 XWayland 上部分场景按 DISPLAY 判定为 X11）。</summary>
    X11,
    /// <summary>原生 Wayland。</summary>
    Wayland
}
