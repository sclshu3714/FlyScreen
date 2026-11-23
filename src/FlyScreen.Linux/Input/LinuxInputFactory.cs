using FlyScreen.Linux.Environment;
using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>
/// Linux 注入工厂。X11 EdgeMouse 主机路径落地时须复用
/// <c>FlyScreen.Services.Input.EdgeMouseVirtualCursor</c>（与 Win 同粘滞/过冲语义）。
/// </summary>
public static class LinuxInputFactory
{
    public static ILinuxInputInjector Create(LinuxSessionKind kind)
        => kind switch
        {
            LinuxSessionKind.X11 => CreateX11(),
            // Wayland 会话多数仍有 XWayland（DISPLAY=）：优先 X11 注入/跟手，穿边拖与键鼠完整可用
            LinuxSessionKind.Wayland => CreateX11OrWaylandStub(),
            _ => new NullInputInjector()
        };

    private static ILinuxInputInjector CreateX11OrWaylandStub()
    {
        var x11 = CreateX11();
        if (x11.IsAvailable)
            return x11;
        return new WaylandInputInjector();
    }

    public static string[] CapsFor(LinuxSessionKind kind, ILinuxInputInjector injector)
    {
        // Wayland + XWayland(X11 注入可用) → 与 X11 桌面同等能力（含 EdgeMouse / 穿边拖跟手）
        var caps = kind switch
        {
            LinuxSessionKind.X11 => PeerCaps.LinuxDesktopX11.ToList(),
            LinuxSessionKind.Wayland when injector is X11InputInjector { IsAvailable: true }
                => PeerCaps.LinuxDesktopX11.ToList(),
            LinuxSessionKind.Wayland => PeerCaps.LinuxDesktopWayland.ToList(),
            _ => PeerCaps.LinuxHeadless.ToList()
        };

        if (!injector.IsAvailable)
        {
            caps.Remove(PeerCaps.InjectInput);
            caps.Remove(PeerCaps.CaptureInput);
            caps.Remove(PeerCaps.EdgeMouse);
            caps.Remove(PeerCaps.AbsPointer);
        }

        return caps.ToArray();
    }

    private static ILinuxInputInjector CreateX11()
    {
        try
        {
            var x11 = new X11InputInjector();
            return x11.IsAvailable ? x11 : new NullInputInjector();
        }
        catch
        {
            return new NullInputInjector();
        }
    }
}
