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
            LinuxSessionKind.Wayland => new WaylandInputInjector(),
            _ => new NullInputInjector()
        };

    public static string[] CapsFor(LinuxSessionKind kind, ILinuxInputInjector injector)
    {
        var caps = kind switch
        {
            LinuxSessionKind.X11 => PeerCaps.LinuxDesktopX11.ToList(),
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
