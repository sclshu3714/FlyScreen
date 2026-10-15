using System.Runtime.InteropServices;
using FlyScreen.Linux.Environment;
using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>
/// X11 键鼠注入（XTest）。真机需 libX11 / libXtst；非 Linux 或库缺失时 IsAvailable=false。
/// </summary>
public sealed class X11InputInjector : ILinuxInputInjector, IDisposable
{
    private IntPtr _display;
    private readonly bool _opened;

    public string BackendName => "x11";
    public bool IsAvailable => _opened && _display != IntPtr.Zero;

    public X11InputInjector()
    {
        if (!LinuxRuntimeEnvironment.IsLinux || string.IsNullOrWhiteSpace(LinuxRuntimeEnvironment.Display))
            return;

        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            _opened = _display != IntPtr.Zero;
            if (_opened)
                _ = XTestQueryExtension(_display, out _, out _, out _, out _);
        }
        catch
        {
            _display = IntPtr.Zero;
            _opened = false;
        }
    }

    public void MoveAbsoluteNormalized(double nx, double ny)
    {
        if (!IsAvailable) return;
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        try
        {
            var screen = XDefaultScreen(_display);
            var w = Math.Max(1, XDisplayWidth(_display, screen));
            var h = Math.Max(1, XDisplayHeight(_display, screen));
            var x = (int)Math.Round(nx * (w - 1));
            var y = (int)Math.Round(ny * (h - 1));
            _ = XTestFakeMotionEvent(_display, screen, x, y, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void MoveRelative(int dx, int dy)
    {
        if (!IsAvailable) return;
        try
        {
            _ = XTestFakeRelativeMotionEvent(_display, dx, dy, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void MouseButton(MouseButtonPayload p)
    {
        if (!IsAvailable) return;
        uint button = p.Button switch { 1 => 3, 2 => 2, _ => 1 };
        try
        {
            _ = XTestFakeButtonEvent(_display, button, p.Down ? 1 : 0, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void MouseWheel(MouseWheelPayload p)
    {
        if (!IsAvailable) return;
        // 4/5 垂直滚轮
        uint button = p.Delta > 0 ? 4u : 5u;
        var clicks = Math.Max(1, Math.Abs(p.Delta) / 120);
        try
        {
            for (var i = 0; i < clicks; i++)
            {
                _ = XTestFakeButtonEvent(_display, button, 1, 0);
                _ = XTestFakeButtonEvent(_display, button, 0, 0);
            }
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void Key(KeyEventPayload p)
    {
        if (!IsAvailable) return;
        // VK→KeySym 完整表后续补齐；此处用拉丁区粗映射
        var keysym = MapVkToKeysym(p.VirtualKey);
        try
        {
            var keycode = XKeysymToKeycode(_display, keysym);
            if (keycode == 0) return;
            _ = XTestFakeKeyEvent(_display, keycode, p.Down ? 1 : 0, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_display != IntPtr.Zero)
        {
            try { _ = XCloseDisplay(_display); } catch { /* ignore */ }
            _display = IntPtr.Zero;
        }
    }

    private static ulong MapVkToKeysym(ushort vk) => vk switch
    {
        0x08 => 0xff08, // BackSpace
        0x09 => 0xff09, // Tab
        0x0D => 0xff0d, // Return
        0x1B => 0xff1b, // Escape
        0x20 => 0x0020, // space
        0x25 => 0xff51, // Left
        0x26 => 0xff52, // Up
        0x27 => 0xff53, // Right
        0x28 => 0xff54, // Down
        >= 0x41 and <= 0x5A => (ulong)(vk + 0x20), // a-z
        >= 0x30 and <= 0x39 => vk,
        _ => vk
    };

    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] private static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport(LibXtst)]
    private static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport(LibXtst)]
    private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);
    [DllImport(LibXtst)]
    private static extern int XTestFakeRelativeMotionEvent(IntPtr display, int dx, int dy, ulong delay);
    [DllImport(LibXtst)]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, ulong delay);
    [DllImport(LibXtst)]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, ulong delay);
}
