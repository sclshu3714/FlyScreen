using System.Runtime.InteropServices;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// 投影前记下主屏模式；停止后只恢复主屏，绝不调用 DisplaySwitch /internal。
/// /internal 会重写系统显示配置，曾导致主屏缩放/分辨率错乱（图标重叠、任务栏异常）。
/// </summary>
public static class DisplayTopologyGuard
{
    private static readonly object Gate = new();
    private static string? _primaryDevice;
    private static DEVMODE _primaryMode;
    private static bool _have;

    public static void CaptureIfEmpty()
    {
        lock (Gate)
        {
            if (_have) return;
            CaptureUnlocked();
        }
    }

    public static void Capture()
    {
        lock (Gate)
            CaptureUnlocked();
    }

    /// <summary>释放虚拟屏后调用：把主屏分辨率/刷新率恢复为投影前快照。</summary>
    public static bool TryRestorePrimary()
    {
        lock (Gate)
        {
            if (!_have || string.IsNullOrEmpty(_primaryDevice))
                return false;

            try
            {
                var dm = _primaryMode;
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL | DM_POSITION;
                // 临时应用，不写注册表（避免再污染）；随后用 UPDATEREGISTRY 固化正确主屏
                var rc = ChangeDisplaySettingsEx(_primaryDevice, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
                if (rc != DISP_CHANGE_SUCCESSFUL && rc != DISP_CHANGE_RESTART)
                {
                    rc = ChangeDisplaySettingsEx(_primaryDevice, ref dm, IntPtr.Zero,
                        CDS_UPDATEREGISTRY, IntPtr.Zero);
                }

                return rc == DISP_CHANGE_SUCCESSFUL || rc == DISP_CHANGE_RESTART;
            }
            catch
            {
                return false;
            }
            finally
            {
                _have = false;
                _primaryDevice = null;
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _have = false;
            _primaryDevice = null;
        }
    }

    private static void CaptureUnlocked()
    {
        try
        {
            for (uint adapter = 0; ; adapter++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, adapter, ref dd, 0))
                    break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;
                if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) == 0)
                    continue;

                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    return;

                _primaryDevice = dd.DeviceName;
                _primaryMode = dm;
                _have = true;
                return;
            }
        }
        catch
        {
            _have = false;
            _primaryDevice = null;
        }
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const uint DM_POSITION = 0x00000020;
    private const uint DM_BITSPERPEL = 0x40000;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

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
