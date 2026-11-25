using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

/// <summary>多显示器工作区定位（画中画默认落在最左屏右侧垂直居中）。</summary>
internal static class VideoWallDisplayPlacement
{
    private const double DefaultPipWidth = 420;
    private const double DefaultPipHeight = 260;
    private const double EdgeMargin = 16;

    public static (double Width, double Height) DefaultPipSize => (DefaultPipWidth, DefaultPipHeight);

    public static Point DefaultPipTopLeft(Visual? dpiVisual)
    {
        var (w, h) = DefaultPipSize;
        var scaleX = 1.0;
        var scaleY = 1.0;
        try
        {
            if (dpiVisual is not null)
            {
                var dpi = VisualTreeHelper.GetDpi(dpiVisual);
                scaleX = dpi.DpiScaleX;
                scaleY = dpi.DpiScaleY;
            }
        }
        catch { /* 未挂树时用 96dpi */ }

        var wa = GetLeftmostWorkAreaDip(scaleX, scaleY);
        return new Point(
            wa.Right - w - EdgeMargin,
            wa.Top + Math.Max(EdgeMargin, (wa.Height - h) / 2));
    }

    public static Rect GetLeftmostWorkAreaDip(double scaleX, double scaleY)
    {
        scaleX = scaleX <= 0 ? 1 : scaleX;
        scaleY = scaleY <= 0 ? 1 : scaleY;

        var monitors = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT _, IntPtr __) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref info))
                    monitors.Add(info.rcWork);
                return true;
            }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            var wa = SystemParameters.WorkArea;
            return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
        }

        var left = monitors.OrderBy(m => m.Left).First();
        return new Rect(
            left.Left / scaleX,
            left.Top / scaleY,
            (left.Right - left.Left) / scaleX,
            (left.Bottom - left.Top) / scaleY);
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
