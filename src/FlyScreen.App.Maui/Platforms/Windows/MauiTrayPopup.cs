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
/// Maui-Win 托盘菜单：单层圆角窗（避免 DWM 外壳 + 内卡片叠成两层）；内容为玄鸟菜单项。
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

        // 内容直接铺满窗口：只有一层，圆角交给 DWM
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

        // 去掉标题栏拖动手柄残留（顶上黑条）
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

        ApplySingleLayerChrome(_hwnd);
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
        // DesiredSize 在未布局时偏小会裁掉「退出」；用内容估算兜底并加圆角余量
        var wDip = Math.Max(188, content.DesiredSize.Width);
        var hDip = Math.Max(EstimateContentHeightDip(), content.DesiredSize.Height + 4);
        var scale = GetDpiScale(_hwnd);
        var widthPx = (int)Math.Ceiling(wDip * scale);
        var heightPx = (int)Math.Ceiling(hDip * scale);

        var left = pt.X - widthPx + (int)(28 * scale);
        var top = pt.Y - heightPx + (int)(8 * scale);
        var wa = GetWorkAreaNear(pt.X, pt.Y);
        left = Math.Max(wa.X + 4, Math.Min(left, wa.X + wa.Width - widthPx - 4));
        top = Math.Max(wa.Y + 4, Math.Min(top, wa.Y + wa.Height - heightPx - 4));

        _appWindow.MoveAndResize(new RectInt32(left, top, widthPx, heightPx));
        ApplySingleLayerChrome(_hwnd);
        BringAboveShell(_hwnd);
        _window.Activate();
        SetForegroundWindow(_hwnd);

        // 激活后再量一次 Actual，偏小则加高（避免末项被裁）
        try
        {
            content.UpdateLayout();
            var actualH = content.ActualHeight;
            if (actualH > 1)
            {
                var needPx = (int)Math.Ceiling((actualH + 2) * scale);
                if (needPx > heightPx)
                {
                    heightPx = needPx;
                    top = Math.Max(wa.Y + 4, Math.Min(pt.Y - heightPx + (int)(8 * scale), wa.Y + wa.Height - heightPx - 4));
                    _appWindow.MoveAndResize(new RectInt32(left, top, widthPx, heightPx));
                }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>4 项×38 + 分隔 + 内边距 + DWM 圆角裁切余量。</summary>
    private static double EstimateContentHeightDip()
        => 4 * 38 + 13 + 6 + 10 + 10;

    private void OnActivated(object sender, WinUi.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WinUi.WindowActivationState.Deactivated)
            CloseMenu();
    }

    private WinUi.UIElement BuildContent()
    {
        // 单层：铺满客户区，不再套 Margin/内圆角卡片（否则会与 DWM 外壳叠成两层）
        var panel = new WinUiControls.Border
        {
            Background = new WinUiMedia.SolidColorBrush(WinUiColor.FromArgb(255, 0xFB, 0xFD, 0xFF)),
            // 不要内容边框：DWM 圆角窗外再画 1px 会像「内边框线」
            BorderThickness = new WinUi.Thickness(0),
            CornerRadius = new WinUi.CornerRadius(0),
            // 底部多留一点，避免末项悬停块被圆角裁切
            Padding = new WinUi.Thickness(4, 6, 4, 10),
            MinWidth = 188
        };

        var stack = new WinUiControls.StackPanel();
        stack.Children.Add(BuildItem(Loc.T("Tray.Open"), _onOpen));
        stack.Children.Add(BuildItem(Loc.T("Tray.Reconnect"), _onReconnect));
        stack.Children.Add(BuildItem(Loc.T("Tray.About"), _onAbout));
        stack.Children.Add(new WinUiControls.Border
        {
            Height = 1,
            Background = Brush(0xD5, 0xE0, 0xEC),
            Margin = new WinUi.Thickness(12, 6, 12, 6)
        });
        stack.Children.Add(BuildItem(Loc.T("Tray.Exit"), _onExit));
        panel.Child = stack;
        return panel;
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
            Height = 36,
            Margin = new WinUi.Thickness(4, 1, 4, 1),
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

    /// <summary>单层外观：仅 DWM 圆角；不用 CS_DROPSHADOW（会在右/下形成硬黑边）。</summary>
    private static void ApplySingleLayerChrome(IntPtr hwnd)
    {
        try
        {
            const int dwmwaWindowCornerPreference = 33;
            const int dwmwcpRound = 2;
            var pref = dwmwcpRound;
            DwmSetWindowAttribute(hwnd, dwmwaWindowCornerPreference, ref pref, sizeof(int));

            // 隐藏 DWM 边框线（内圈描边）
            const int dwmwaBorderColor = 34;
            const int dwmwaColorNone = unchecked((int)0xFFFFFFFE);
            var borderNone = dwmwaColorNone;
            DwmSetWindowAttribute(hwnd, dwmwaBorderColor, ref borderNone, sizeof(int));
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
