using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.ScreenExtend;

/// <summary>主屏参数读取，以及把虚拟副屏分辨率切到指定宽高。</summary>
public static class DisplayModeHelper
{
    public readonly record struct PrimaryDisplayInfo(int Width, int Height, int RefreshHz, uint Dpi, double Scale)
    {
        public int ScalePercent => (int)Math.Round(Scale * 100);
    }

    public static PrimaryDisplayInfo GetPrimaryInfo()
    {
        var primary = DisplayMonitorEnum.FindPrimary()
                      ?? new MonitorRect(0, 0, 1920, 1080, true);
        var refresh = TryGetRefreshRate(primary) ?? 60;
        var dpi = TryGetDpi(primary.Left + primary.Width / 2, primary.Top + primary.Height / 2) ?? 96;
        var scale = Math.Round(dpi / 96.0, 2);
        return new PrimaryDisplayInfo(primary.Width, primary.Height, refresh, dpi, scale);
    }

    public static Services.Network.DisplayInfoPayload ToDisplayInfoPayload(PrimaryDisplayInfo? info = null)
    {
        var p = info ?? GetPrimaryInfo();
        return new Services.Network.DisplayInfoPayload
        {
            Width = p.Width,
            Height = p.Height,
            ScalePercent = Services.ScreenExtend.ProjectionDisplayPresets.ClampScale(p.ScalePercent),
            RefreshHz = p.RefreshHz
        };
    }

    /// <summary>
    /// 将非主显示器改为指定宽高，并可按布局方位放到主屏左/右/上/下。
    /// <paramref name="side"/> 与穿边 peerSide 一致。
    /// </summary>
    public static bool TrySetSecondaryMode(
        int width,
        int height,
        int refreshHz,
        Services.ScreenExtend.LayoutEdgeSide side = Services.ScreenExtend.LayoutEdgeSide.Unknown)
    {
        try
        {
            var primary = DisplayMonitorEnum.FindPrimary();
            for (uint adapter = 0; ; adapter++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, adapter, ref dd, 0))
                    break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;
                if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                    continue;

                var dm = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf<DEVMODE>(),
                    dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL,
                    dmPelsWidth = width,
                    dmPelsHeight = height,
                    dmDisplayFrequency = (uint)(refreshHz > 0 ? refreshHz : 60),
                    dmBitsPerPel = 32
                };

                var probe = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                for (var i = 0; EnumDisplaySettings(dd.DeviceName, i, ref probe); i++)
                {
                    if (probe.dmPelsWidth == width && probe.dmPelsHeight == height)
                    {
                        dm.dmDisplayFrequency = probe.dmDisplayFrequency > 0
                            ? probe.dmDisplayFrequency
                            : dm.dmDisplayFrequency;
                        break;
                    }
                }

                if (side != Services.ScreenExtend.LayoutEdgeSide.Unknown && primary != null)
                {
                    var p = primary.Value;
                    var (px, py) = side switch
                    {
                        Services.ScreenExtend.LayoutEdgeSide.Left => (p.Left - width, p.Top),
                        Services.ScreenExtend.LayoutEdgeSide.Right => (p.Left + p.Width, p.Top),
                        Services.ScreenExtend.LayoutEdgeSide.Top => (p.Left, p.Top - height),
                        Services.ScreenExtend.LayoutEdgeSide.Bottom => (p.Left, p.Top + p.Height),
                        _ => (p.Left + p.Width, p.Top)
                    };
                    dm.dmFields |= DM_POSITION;
                    dm.dmPositionX = px;
                    dm.dmPositionY = py;
                }

                // 仅会话内临时改副屏，禁止 CDS_UPDATEREGISTRY，避免污染主屏/系统显示配置
                var rc = ChangeDisplaySettingsEx(dd.DeviceName, ref dm, IntPtr.Zero, CDS_NORESET, IntPtr.Zero);
                ChangeDisplaySettingsEx(null!, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                if (rc != DISP_CHANGE_SUCCESSFUL && rc != DISP_CHANGE_RESTART)
                    rc = ChangeDisplaySettingsEx(dd.DeviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
                return rc == DISP_CHANGE_SUCCESSFUL || rc == DISP_CHANGE_RESTART;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>已停用：未文档化 DisplayConfig DPI API 曾误改主屏缩放，导致桌面错乱。</summary>
    public static bool TryMatchSecondaryDpiScale(double primaryScale) => false;

    private static int? TryGetRefreshRate(MonitorRect monitor)
    {
        try
        {
            for (uint adapter = 0; ; adapter++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, adapter, ref dd, 0))
                    break;
                if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) == 0) continue;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)
                    && dm.dmDisplayFrequency > 1)
                    return (int)dm.dmDisplayFrequency;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static uint? TryGetDpi(int x, int y)
    {
        try
        {
            var hmon = MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return null;
            if (GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                return dx;
        }
        catch { /* ignore */ }

        return null;
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const uint DM_POSITION = 0x00000020;
    private const uint DM_BITSPERPEL = 0x40000;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_NORESET = 0x10000000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public uint dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
