using System.Runtime.InteropServices;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>托盘：对齐 WPF NotifyIcon（打开 / 重连 / 关于 / 退出 + 双击打开）。</summary>
public sealed class MauiTrayService : IDisposable
{
    private const int NimAdd = 0x00000000;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int WmApp = 0x8000;
    private const int CallbackMsg = WmApp + 1;
    private const int WmCommand = 0x0111;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;

    private const int IdOpen = 1001;
    private const int IdReconnect = 1002;
    private const int IdAbout = 1003;
    private const int IdExit = 1004;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownIcon;
    private WndProc? _proc;
    private Action? _show;
    private Action? _reconnect;
    private Action? _about;
    private Action? _exit;
    private bool _added;

    public void Ensure(Action show, Action reconnect, Action about, Action exit)
    {
        _show = show;
        _reconnect = reconnect;
        _about = about;
        _exit = exit;
        if (_hwnd != IntPtr.Zero) return;

        _proc = WndProcImpl;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "FlyScreenMauiTray"
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, "FlyScreenMauiTray", "FlyScreenTray", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _icon = LoadAppIcon();
        if (_icon == IntPtr.Zero)
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        else
            _ownIcon = true;

        var data = BuildData(Loc.T("Tray.Tip"));
        Shell_NotifyIcon(NimAdd, ref data);
        _added = true;
    }

    public void Dispose()
    {
        if (_added && _hwnd != IntPtr.Zero)
        {
            var data = BuildData("");
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_ownIcon && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
            _ownIcon = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static IntPtr LoadAppIcon()
    {
        foreach (var path in CandidateIconPaths())
        {
            if (!File.Exists(path)) continue;
            var h = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
            if (h != IntPtr.Zero) return h;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateIconPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        // 优先 Assets：带透明通道的手绘 ico；根目录 appicon.ico 可能是 MauiIcon 生成的实心底
        yield return Path.Combine(baseDir, "Assets", "appicon.ico");
        yield return Path.Combine(baseDir, "Resources", "AppIcon", "appicon.ico");
        yield return Path.Combine(baseDir, "appicon.ico");
    }

    private NotifyIconData BuildData(string tip)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMsg,
            hIcon = _icon,
            szTip = tip
        };
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, IdOpen, Loc.T("Tray.Open"));
        AppendMenu(menu, MfString, IdReconnect, Loc.T("Tray.Reconnect"));
        AppendMenu(menu, MfString, IdAbout, Loc.T("Tray.About"));
        AppendMenu(menu, MfSeparator, 0, string.Empty);
        AppendMenu(menu, MfString, IdExit, Loc.T("Tray.Exit"));

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, TpmRightButton, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMsg)
        {
            var mouse = lParam.ToInt32() & 0xFFFF;
            if (mouse is WmLButtonDblClk)
                MainThread.BeginInvokeOnMainThread(() => _show?.Invoke());
            else if (mouse is WmRButtonUp or WmContextMenu)
                MainThread.BeginInvokeOnMainThread(ShowContextMenu);
            return IntPtr.Zero;
        }

        if (msg == WmCommand)
        {
            var id = wParam.ToInt32() & 0xFFFF;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                switch (id)
                {
                    case IdOpen: _show?.Invoke(); break;
                    case IdReconnect: _reconnect?.Invoke(); break;
                    case IdAbout: _about?.Invoke(); break;
                    case IdExit: _exit?.Invoke(); break;
                }
            });
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point lpPoint);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);
}
