using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// Windows 无边框窗默认是矩形客户区。圆角画在内容上时，四角像素仍属于窗口。
/// 用窗口区域把直角裁掉，桌面才能从四角透出来。
/// </summary>
internal static class WindowsWindowCorner
{
    public static bool Apply(Window window, double radiusDip)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == 0)
            return false;

        var square = radiusDip <= 0
                     || window.WindowState is WindowState.Maximized or WindowState.FullScreen;
        if (square)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return true;
        }

        if (!GetWindowRect(hwnd, out var rc))
            return false;

        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w < 2 || h < 2)
            return false;

        var scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var diameter = Math.Max(2, (int)Math.Round(radiusDip * 2 * scale));
        // CreateRoundRectRgn 的右/下边界是排除的，+1 才能盖住窗口最外一圈像素。
        var rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
        if (rgn == IntPtr.Zero)
            return false;

        if (SetWindowRgn(hwnd, rgn, true) == 0)
        {
            DeleteObject(rgn);
            return false;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
