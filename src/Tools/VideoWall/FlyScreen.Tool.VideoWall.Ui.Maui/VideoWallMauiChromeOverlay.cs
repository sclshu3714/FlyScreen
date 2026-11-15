#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Maui-Win 视频 HWND 会盖住同窗 MAUI 控件。用两个无边框工具窗承载顶/底 chrome，
/// 视频宿主保持 Margin=0 全铺。窗口只创建一次，之后 Show/Hide，避免闪窗与二次播放损坏。
/// </summary>
internal sealed class VideoWallMauiChromeOverlay : IDisposable
{
    private readonly VisualElement _anchor;
    private Window? _titleWindow;
    private Window? _transportWindow;
    private ContentPage? _titlePage;
    private ContentPage? _transportPage;
    private View? _titleContent;
    private View? _transportContent;
    private bool _titleVisible;
    private bool _transportVisible;
    private bool _disposed;
    private bool _created;
    private IDispatcherTimer? _syncTimer;

    public VideoWallMauiChromeOverlay(VisualElement anchor) => _anchor = anchor;

    public void SetContents(View title, View transport)
    {
        _titleContent = title;
        _transportContent = transport;
    }

    public void Show(bool title, bool transport)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCreated();
        _titleVisible = title;
        _transportVisible = transport;
        ApplyVisibility();
        SyncBounds();
        StartSyncTimer();
    }

    public void Hide()
    {
        _titleVisible = false;
        _transportVisible = false;
        ApplyVisibility();
        StopSyncTimer();
    }

    public void SyncBounds()
    {
        if (_disposed || !_created) return;
        if (!TryGetAnchorScreenRect(out var x, out var y, out var w, out var h) || w < 8 || h < 8)
            return;

        const int titleH = 56;
        const int transportH = 128;

        MoveWindow(_titleWindow, x, y, w, Math.Min(titleH, h));
        var th = Math.Min(transportH, h);
        MoveWindow(_transportWindow, x, y + h - th, w, th);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSyncTimer();
        CloseWindow(ref _titleWindow, ref _titlePage);
        CloseWindow(ref _transportWindow, ref _transportPage);
        _titleContent = null;
        _transportContent = null;
        _created = false;
    }

    private void EnsureCreated()
    {
        if (_created) return;
        if (_titleContent is null || _transportContent is null) return;
        if (Application.Current is null) return;

        _titleWindow = CreateStripWindow(_titleContent, isTitle: true, out _titlePage);
        _transportWindow = CreateStripWindow(_transportContent, isTitle: false, out _transportPage);
        _created = true;

        // 先藏到屏外并 Hide，避免 OpenWindow 时闪一下
        MoveWindow(_titleWindow, -32000, -32000, 8, 8);
        MoveWindow(_transportWindow, -32000, -32000, 8, 8);
        SetWindowVisible(_titleWindow, false);
        SetWindowVisible(_transportWindow, false);
    }

    private void ApplyVisibility()
    {
        SetWindowVisible(_titleWindow, _titleVisible && _titleContent is not null);
        SetWindowVisible(_transportWindow, _transportVisible && _transportContent is not null);
    }

    private Window CreateStripWindow(View content, bool isTitle, out ContentPage page)
    {
        DetachFromParent(content);

        var stripBg = isTitle
            ? Color.FromArgb("#E60B1220")
            : Color.FromArgb("#F20B1220");

        page = new ContentPage
        {
            Title = isTitle ? "VideoWallTitleChrome" : "VideoWallTransportChrome",
            BackgroundColor = stripBg,
            Padding = 0,
            Content = content
        };

        var window = new Window(page)
        {
            Title = isTitle ? "FlyScreen.VideoWall.TitleChrome" : "FlyScreen.VideoWall.TransportChrome",
            Width = 8,
            Height = 8,
            X = -32000,
            Y = -32000
        };

        Application.Current!.OpenWindow(window);
        window.HandlerChanged += (_, _) => ConfigureNativeWindow(window);
        if (window.Handler is not null)
            ConfigureNativeWindow(window);

        return window;
    }

    private static void DetachFromParent(View content)
    {
        switch (content.Parent)
        {
            case Layout oldParent:
                oldParent.Children.Remove(content);
                break;
            case ContentPage pageParent:
                pageParent.Content = null;
                break;
            case ContentView cv:
                cv.Content = null;
                break;
            case Border b:
                b.Content = null;
                break;
        }
    }

    private static void ConfigureNativeWindow(Window window)
    {
        if (window.Handler?.PlatformView is not MauiWinUIWindow native)
            return;

        try
        {
            native.ExtendsContentIntoTitleBar = false;
            try { native.SystemBackdrop = null; } catch { /* ignore */ }

            var hwnd = WindowNative.GetWindowHandle(native);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }

            // 去掉 / 同化边框：COLOR_NONE + 边框厚度 0；再刷成与条带同色，避免残白线
            uint colorNone = 0xFFFFFFFE; // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorNone, sizeof(uint));
            uint borderThickness = 0;
            DwmSetWindowAttribute(hwnd, DwmwaVisibleFrameBorderThickness, ref borderThickness, sizeof(uint));
            var corner = DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(uint));
            // 若系统仍画边，用与 HUD 同色盖住（COLORREF = 0x00BBGGRR）
            uint darkBorder = 0x0020120B;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref darkBorder, sizeof(uint));

            var darkBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 11, 18, 32));
            switch (native.Content)
            {
                case Microsoft.UI.Xaml.Controls.Panel panel:
                    panel.Background = darkBrush;
                    break;
                case Microsoft.UI.Xaml.Controls.Border border:
                    border.Background = darkBrush;
                    break;
                case Microsoft.UI.Xaml.Controls.Control control:
                    control.Background = darkBrush;
                    break;
            }

            // 不用 LWA_ALPHA：分层窗在 WinUI 上容易出现白边伪影；半透明交给 MAUI 深色底
            var ex = GetWindowLong(hwnd, GwlExStyle);
            ex = (ex | WsExToolWindow | WsExNoActivate) & ~WsExLayered;
            SetWindowLong(hwnd, GwlExStyle, ex);
            SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch
        {
            /* 原生窗尚未就绪 */
        }
    }

    private static void MoveWindow(Window? window, int x, int y, int w, int h)
    {
        if (window?.Handler?.PlatformView is not MauiWinUIWindow native)
            return;
        try
        {
            ConfigureNativeWindow(window);
            native.AppWindow?.MoveAndResize(new RectInt32(x, y, Math.Max(1, w), Math.Max(1, h)));
        }
        catch
        {
            /* ignore */
        }
    }

    private static void SetWindowVisible(Window? window, bool visible)
    {
        if (window?.Handler?.PlatformView is not MauiWinUIWindow native)
            return;
        try
        {
            if (visible)
            {
                native.AppWindow?.Show(false);
                ConfigureNativeWindow(window);
            }
            else
            {
                native.AppWindow?.Hide();
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static void CloseWindow(ref Window? window, ref ContentPage? page)
    {
        if (window is null) return;
        try
        {
            if (page is not null)
                page.Content = null;
            Application.Current?.CloseWindow(window);
        }
        catch
        {
            /* ignore */
        }

        window = null;
        page = null;
    }

    private void StartSyncTimer()
    {
        if (_syncTimer is not null) return;
        _syncTimer = _anchor.Dispatcher.CreateTimer();
        _syncTimer.Interval = TimeSpan.FromMilliseconds(64);
        _syncTimer.Tick += (_, _) => SyncBounds();
        _syncTimer.Start();
    }

    private void StopSyncTimer()
    {
        _syncTimer?.Stop();
        _syncTimer = null;
    }

    private bool TryGetAnchorScreenRect(out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (_anchor.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement fe)
            return false;
        if (fe.XamlRoot is null || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
            return false;

        try
        {
            var transform = fe.TransformToVisual(null);
            var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            var scale = fe.XamlRoot.RasterizationScale;

            var parentWindow = _anchor.Window ?? Application.Current?.Windows.FirstOrDefault();
            if (parentWindow?.Handler?.PlatformView is not MauiWinUIWindow mauiWin)
                return false;

            var hwnd = WindowNative.GetWindowHandle(mauiWin);
            if (!GetWindowRect(hwnd, out var rect))
                return false;

            var clientOrigin = new PointWin();
            if (!ClientToScreen(hwnd, ref clientOrigin))
            {
                clientOrigin.X = rect.Left;
                clientOrigin.Y = rect.Top;
            }

            x = clientOrigin.X + (int)Math.Round(topLeft.X * scale);
            y = clientOrigin.Y + (int)Math.Round(topLeft.Y * scale);
            w = Math.Max(1, (int)Math.Round(fe.ActualWidth * scale));
            h = Math.Max(1, (int)Math.Round(fe.ActualHeight * scale));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExLayered = 0x00080000;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaVisibleFrameBorderThickness = 37;
    private const uint DwmwcpDoNotRound = 1;
    private static readonly IntPtr HwndTop = new(0);
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectWin lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref PointWin lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectWin
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointWin
    {
        public int X, Y;
    }
}
#endif
