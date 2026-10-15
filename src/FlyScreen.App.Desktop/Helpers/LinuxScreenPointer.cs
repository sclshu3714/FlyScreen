using System.Runtime.InteropServices;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// X11 屏幕坐标下的指针位置（与窗体 Position 无关），用于自绘标题栏平滑拖动。
/// </summary>
internal static class LinuxScreenPointer
{
    private static IntPtr _display;
    private static IntPtr _root;
    private static bool _failed;

    public static bool TryGetPosition(out int x, out int y)
    {
        x = y = 0;
        if (_failed || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            EnsureDisplay();
            if (_display == IntPtr.Zero || _root == IntPtr.Zero)
                return false;

            var ok = XQueryPointer(
                _display, _root,
                out _, out _,
                out x, out y,
                out _, out _,
                out _) != 0;
            return ok;
        }
        catch
        {
            _failed = true;
            return false;
        }
    }

    private static void EnsureDisplay()
    {
        if (_display != IntPtr.Zero) return;
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            _failed = true;
            return;
        }

        _root = XDefaultRootWindow(_display);
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(
        IntPtr display, IntPtr window,
        out IntPtr rootReturn, out IntPtr childReturn,
        out int rootX, out int rootY,
        out int winX, out int winY,
        out int mask);
}
