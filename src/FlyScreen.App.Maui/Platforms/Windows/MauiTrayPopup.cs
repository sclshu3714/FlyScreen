#if WINDOWS
using System.Runtime.InteropServices;
using FlyScreen.Helpers.Localization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;
using WinUi = Microsoft.UI.Xaml;
using WinUiControls = Microsoft.UI.Xaml.Controls;
using WinUiMedia = Microsoft.UI.Xaml.Media;
using WinUiColor = Windows.UI.Color;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// Maui-Win 托盘菜单：白底铺满客户区（避免右/下露黑）、四边等距内边距；关系统硬阴影。
/// </summary>
internal sealed class MauiTrayPopup
{
    private static MauiTrayPopup? _open;

    private readonly WinUi.Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly Action _onOpen;
    private readonly Action _onReconnect;
    private readonly Action _onAbout;
    private readonly Action _onExit;
    private bool _closing;

    // 与 WPF TrayContextMenuWindow 内容区对齐：Stack (4,6,4,6) + 行 Margin(4,1)
    private const double PanelPadX = 8;
    private const double PanelPadY = 8;
    private const double ItemHeight = 36;
    private const double ItemVMargin = 1;
    private const double SepHeight = 1;
    private const double SepVMargin = 6;

    private MauiTrayPopup(
        Action onOpen,
        Action onReconnect,
        Action onAbout,
        Action onExit)
    {
        _onOpen = onOpen;
        _onReconnect = onReconnect;
        _onAbout = onAbout;
        _onExit = onExit;

        _window = new WinUi.Window
        {
            Title = "FlyScreenTrayMenu",
            Content = BuildContent()
        };
        _window.Activated += OnActivated;

        _hwnd = WindowNative.GetWindowHandle(_window);
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(false, false);
        }

        try
        {
            _window.ExtendsContentIntoTitleBar = true;
            _window.SetTitleBar(null);
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
                _appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                _appWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
            }
        }
        catch { /* ignore */ }

        ApplyChrome(_hwnd);
    }

    public static void ShowNearCursor(
        Action onOpen,
        Action onReconnect,
        Action onAbout,
        Action onExit)
    {
        try { _open?.CloseMenu(); } catch { /* ignore */ }

        var popup = new MauiTrayPopup(onOpen, onReconnect, onAbout, onExit);
        _open = popup;
        popup.ShowAtCursor();
    }

    private void ShowAtCursor()
    {
        GetCursorPos(out var pt);
        var content = (WinUi.FrameworkElement)_window.Content;
        content.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        // 紧贴内容尺寸，不加「余量」——余量会露出默认黑底，看起来像右/下硬阴影
        var wDip = Math.Max(188, content.DesiredSize.Width);
        var hDip = Math.Max(EstimateContentHeightDip(), content.DesiredSize.Height);
        var scale = GetDpiScale(_hwnd);
        var widthPx = Math.Max(1, (int)Math.Ceiling(wDip * scale));
        var heightPx = Math.Max(1, (int)Math.Ceiling(hDip * scale));

        var left = pt.X - widthPx + (int)(28 * scale);
        var top = pt.Y - heightPx + (int)(8 * scale);
        var wa = GetWorkAreaNear(pt.X, pt.Y);
        left = Math.Max(wa.X + 4, Math.Min(left, wa.X + wa.Width - widthPx - 4));
        top = Math.Max(wa.Y + 4, Math.Min(top, wa.Y + wa.Height - heightPx - 4));

        _appWindow.MoveAndResize(new RectInt32(left, top, widthPx, heightPx));
        ApplyChrome(_hwnd);
        BringAboveShell(_hwnd);
        _window.Activate();
        SetForegroundWindow(_hwnd);

        try
        {
            content.UpdateLayout();
            // 仅当内容实际更高才加高；绝不因「预留」把窗体撑出黑边
            var needW = Math.Max(1, (int)Math.Ceiling(Math.Max(wDip, content.ActualWidth) * scale));
            var needH = Math.Max(1, (int)Math.Ceiling(Math.Max(hDip, content.ActualHeight) * scale));
            if (needW != widthPx || needH != heightPx)
            {
                widthPx = needW;
                heightPx = needH;
                left = Math.Max(wa.X + 4, Math.Min(pt.X - widthPx + (int)(28 * scale), wa.X + wa.Width - widthPx - 4));
                top = Math.Max(wa.Y + 4, Math.Min(pt.Y - heightPx + (int)(8 * scale), wa.Y + wa.Height - heightPx - 4));
                _appWindow.MoveAndResize(new RectInt32(left, top, widthPx, heightPx));
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>4 项×(36+2) + 分隔(1+12) + 面板上下各 8。</summary>
    private static double EstimateContentHeightDip()
        => 4 * (ItemHeight + ItemVMargin * 2) + (SepHeight + SepVMargin * 2) + PanelPadY * 2;

    private void OnActivated(object sender, WinUi.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WinUi.WindowActivationState.Deactivated)
            CloseMenu();
    }

    private WinUi.UIElement BuildContent()
    {
        var bg = Brush(0xFB, 0xFD, 0xFF);
        // 铺满客户区：Actual 小于窗体时也不会露系统黑底
        var panel = new WinUiControls.Border
        {
            Background = bg,
            BorderThickness = new WinUi.Thickness(0),
            CornerRadius = new WinUi.CornerRadius(0),
            Padding = new WinUi.Thickness(PanelPadX, PanelPadY, PanelPadX, PanelPadY),
            MinWidth = 188,
            HorizontalAlignment = WinUi.HorizontalAlignment.Stretch,
            VerticalAlignment = WinUi.VerticalAlignment.Stretch
        };

        var stack = new WinUiControls.StackPanel();
        stack.Children.Add(BuildItem(Loc.T("Tray.Open"), _onOpen));
        stack.Children.Add(BuildItem(Loc.T("Tray.Reconnect"), _onReconnect));
        stack.Children.Add(BuildItem(Loc.T("Tray.About"), _onAbout));
        stack.Children.Add(new WinUiControls.Border
        {
            Height = SepHeight,
            Background = Brush(0xD5, 0xE0, 0xEC),
            Margin = new WinUi.Thickness(12, SepVMargin, 12, SepVMargin),
            HorizontalAlignment = WinUi.HorizontalAlignment.Stretch
        });
        stack.Children.Add(BuildItem(Loc.T("Tray.Exit"), _onExit));
        panel.Child = stack;

        // 外层再铺一层同色，保证 Window 默认底被完全盖住
        var root = new WinUiControls.Grid { Background = bg };
        root.Children.Add(panel);
        return root;
    }

    private WinUi.UIElement BuildItem(string label, Action action)
    {
        var hoverBg = Brush(0xE8, 0xF2, 0xFF);
        var hoverBorder = Brush(0xA9, 0xC3, 0xDE);
        var transparent = new WinUiMedia.SolidColorBrush(WinUiColor.FromArgb(0, 0, 0, 0));
        var accentBrush = Brush(0x25, 0x63, 0xEB);

        var accent = new WinUiControls.Border
        {
            Width = 3,
            Height = 20,
            CornerRadius = new WinUi.CornerRadius(1.5),
            Background = transparent,
            HorizontalAlignment = WinUi.HorizontalAlignment.Left,
            VerticalAlignment = WinUi.VerticalAlignment.Center,
            Margin = new WinUi.Thickness(-8, 0, 0, 0)
        };
        var text = new WinUiControls.TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Brush(0x11, 0x18, 0x27),
            VerticalAlignment = WinUi.VerticalAlignment.Center
        };
        var inner = new WinUiControls.Grid();
        inner.Children.Add(accent);
        inner.Children.Add(text);

        var row = new WinUiControls.Border
        {
            Background = transparent,
            BorderBrush = transparent,
            BorderThickness = new WinUi.Thickness(1),
            CornerRadius = new WinUi.CornerRadius(8),
            Padding = new WinUi.Thickness(14, 0, 14, 0),
            Height = ItemHeight,
            Margin = new WinUi.Thickness(4, ItemVMargin, 4, ItemVMargin),
            HorizontalAlignment = WinUi.HorizontalAlignment.Stretch,
            Child = inner
        };
        row.PointerEntered += (_, _) =>
        {
            row.Background = hoverBg;
            row.BorderBrush = hoverBorder;
            accent.Background = accentBrush;
        };
        row.PointerExited += (_, _) =>
        {
            row.Background = transparent;
            row.BorderBrush = transparent;
            accent.Background = transparent;
        };
        row.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.PointerUpdateKind !=
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
                return;
            e.Handled = true;
            CloseMenu();
            try { action(); } catch { /* ignore */ }
        };
        return row;
    }

    private static WinUiMedia.SolidColorBrush Brush(byte r, byte g, byte b)
        => new(WinUiColor.FromArgb(255, r, g, b));

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        if (ReferenceEquals(_open, this))
            _open = null;
        try
        {
            _window.Activated -= OnActivated;
            _window.Close();
        }
        catch { /* ignore */ }
    }

    /// <summary>DWM 圆角 + 去系统边框线/backdrop（勿改窗口类 CS_*，会牵连主窗）。</summary>
    private static void ApplyChrome(IntPtr hwnd)
    {
        try
        {
            const int dwmwaWindowCornerPreference = 33;
            const int dwmwcpRound = 2;
            var pref = dwmwcpRound;
            DwmSetWindowAttribute(hwnd, dwmwaWindowCornerPreference, ref pref, sizeof(int));

            const int dwmwaBorderColor = 34;
            const int dwmwaColorNone = unchecked((int)0xFFFFFFFE);
            var borderNone = dwmwaColorNone;
            DwmSetWindowAttribute(hwnd, dwmwaBorderColor, ref borderNone, sizeof(int));

            // Win11：关掉系统 backdrop，避免额外暗边
            const int dwmwaSystemBackdropType = 38;
            const int dwmsbtNone = 1;
            var backdrop = dwmsbtNone;
            DwmSetWindowAttribute(hwnd, dwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch { /* ignore */ }
    }

    private static void BringAboveShell(IntPtr hwnd)
    {
        try
        {
            const int hwndTopmost = -1;
            const uint swpNomove = 0x0002;
            const uint swpNosize = 0x0001;
            const uint swpShowwindow = 0x0040;
            SetWindowPos(hwnd, (IntPtr)hwndTopmost, 0, 0, 0, 0, swpNomove | swpNosize | swpShowwindow);
            if (AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Presenter is OverlappedPresenter p)
                p.IsAlwaysOnTop = true;
        }
        catch { /* ignore */ }
    }

    private static double GetDpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    private static RectInt32 GetWorkAreaNear(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var r = info.rcWork;
            return new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        return new RectInt32(0, 0, 1920, 1080);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
#endif
