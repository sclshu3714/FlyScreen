using System.Reflection;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// 电脑端画中画：MAUI 独立 <see cref="Window"/>，浮到桌面（可压在其它应用上）。
/// 工具 UI 按 net10.0 打包，置顶等 WinUI API 用运行时反射。
/// </summary>
internal sealed class VideoWallMauiDesktopFloatWindow
{
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 260;
    private const double EdgeMargin = 16;

    private Window? _window;
    private ContentPage? _page;
    private View? _content;
    private Rect _pipBounds;
    private Mode _mode = Mode.Pip;
    private bool _closing;
    private bool _applyingMode;

    public enum Mode { Pip, Maximized, Fullscreen }

    public bool IsOpen => _window is not null;
    public Mode CurrentMode => _mode;
    public event Action? Closed;

    public void Open(View content, string title, Mode mode)
    {
        if (_window is not null)
        {
            ApplyMode(mode);
            return;
        }

        _content = content;
        _page = new ContentPage
        {
            Content = content,
            BackgroundColor = Colors.Black,
            Padding = 0
        };
        NavigationPage.SetHasNavigationBar(_page, false);

        if (_pipBounds.Width <= 0 || _pipBounds.Height <= 0)
            _pipBounds = ComputeDefaultPipBounds();

        _window = new Window(_page)
        {
            Title = string.IsNullOrWhiteSpace(title)
                ? Loc.T("Tool.VideoWall.PictureInPicture")
                : $"{title} · {Loc.T("Tool.VideoWall.PictureInPicture")}",
            Width = _pipBounds.Width,
            Height = _pipBounds.Height,
            X = _pipBounds.X,
            Y = _pipBounds.Y
        };

        _window.Destroying += OnWindowDestroying;
        _window.HandlerChanged += OnWindowHandlerChanged;

        var app = Application.Current;
        if (app is null)
            throw new InvalidOperationException("Application.Current is null");

        _mode = mode;
        app.OpenWindow(_window);
        // Handler 未就绪时原生 Maximize 会空操作；HandlerChanged 会再 ApplyMode
        ApplyMode(mode);
    }

    public void ApplyMode(Mode mode)
    {
        _mode = mode;
        var win = _window;
        if (win is null || _applyingMode) return;

        _applyingMode = true;
        try
        {
            if (mode == Mode.Pip)
            {
                if (_pipBounds.Width > 0 && _pipBounds.Height > 0)
                {
                    win.Width = _pipBounds.Width;
                    win.Height = _pipBounds.Height;
                    win.X = _pipBounds.X;
                    win.Y = _pipBounds.Y;
                }

                TrySetPresenterState(win, maximized: false, fullscreen: false, alwaysOnTop: true);
                return;
            }

            RememberPipBounds();
            // 先把 MAUI 窗几何拉满，再走 WinUI Presenter（Maximize / FullScreen）
            TryFillDisplay(win);
            TrySetPresenterState(
                win,
                maximized: mode == Mode.Maximized,
                fullscreen: mode == Mode.Fullscreen,
                alwaysOnTop: true);
        }
        finally
        {
            _applyingMode = false;
        }
    }

    public void Close()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            RememberPipBounds();
            var win = _window;
            _window = null;
            if (_page is not null)
                _page.Content = null;
            _page = null;
            _content = null;

            if (win is not null)
            {
                win.Destroying -= OnWindowDestroying;
                win.HandlerChanged -= OnWindowHandlerChanged;
                try { Application.Current?.CloseWindow(win); }
                catch { /* ignore */ }
            }
        }
        finally
        {
            _closing = false;
            Closed?.Invoke();
        }
    }

    public void RememberPipBounds()
    {
        var win = _window;
        if (win is null || _mode != Mode.Pip) return;
        if (win.Width > 0 && win.Height > 0)
            _pipBounds = new Rect(win.X, win.Y, win.Width, win.Height);
    }

    private void OnWindowDestroying(object? sender, EventArgs e)
    {
        if (_closing) return;
        // 用户点系统关闭：通知外层还原播放器
        _window = null;
        _page = null;
        _content = null;
        Closed?.Invoke();
    }

    private void OnWindowHandlerChanged(object? sender, EventArgs e)
    {
        // 原生句柄就绪后按当前模式重应用（勿强制回到 PiP）
        if (_window is not null)
            ApplyMode(_mode);
    }

    private static void TryFillDisplay(Window win)
    {
        try
        {
            var di = DeviceDisplay.MainDisplayInfo;
            var w = di.Width / di.Density;
            var h = di.Height / di.Density;
            if (w <= 1 || h <= 1) return;
            win.X = 0;
            win.Y = 0;
            win.Width = w;
            win.Height = h;
        }
        catch
        {
            /* ignore */
        }
    }

    private static Rect ComputeDefaultPipBounds()
    {
        var (w, h) = (DefaultWidth, DefaultHeight);
        try
        {
            var di = DeviceDisplay.MainDisplayInfo;
            var screenW = di.Width / di.Density;
            var screenH = di.Height / di.Density;
            var x = Math.Max(EdgeMargin, screenW - w - EdgeMargin);
            var y = Math.Max(EdgeMargin, (screenH - h) / 2);
            return new Rect(x, y, w, h);
        }
        catch
        {
            return new Rect(80, 80, w, h);
        }
    }

    /// <summary>WinUI AppWindow：置顶 / 最大化 / FullScreen Presenter。</summary>
    private static void TrySetPresenterState(Window window, bool maximized, bool fullscreen, bool alwaysOnTop)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var platformView = window.Handler?.PlatformView;
            if (platformView is null)
                return;

            // Microsoft.UI.Xaml.Window → HWND → AppWindow
            var windowNative = Type.GetType("WinRT.Interop.WindowNative, Microsoft.Windows.SDK.NET")
                               ?? Type.GetType("WinRT.Interop.WindowNative, WinRT.Runtime");
            var getHandle = windowNative?.GetMethod("GetWindowHandle", BindingFlags.Public | BindingFlags.Static);
            object? hwnd;
            if (getHandle is null)
            {
                var hwndProp = platformView.GetType().GetProperty("WindowHandle")
                               ?? platformView.GetType().GetProperty("Hwnd");
                hwnd = hwndProp?.GetValue(platformView);
            }
            else
            {
                hwnd = getHandle.Invoke(null, [platformView]);
            }

            if (hwnd is null) return;
            ApplyAppWindowFromHwnd(hwnd, maximized, fullscreen, alwaysOnTop);
        }
        catch
        {
            /* 非 WinUI 宿主时忽略 */
        }
    }

    private static void ApplyAppWindowFromHwnd(
        object hwnd,
        bool maximized,
        bool fullscreen,
        bool alwaysOnTop)
    {
        var win32Interop = Type.GetType("Microsoft.UI.Win32Interop, Microsoft.Windows.SDK.NET")
                           ?? Type.GetType("Microsoft.UI.Win32Interop, Microsoft.UI");
        var getWindowId = win32Interop?.GetMethod(
            "GetWindowIdFromWindow",
            BindingFlags.Public | BindingFlags.Static);
        if (getWindowId is null) return;

        var windowId = getWindowId.Invoke(null, [hwnd]);
        if (windowId is null) return;

        var appWindowType = Type.GetType("Microsoft.UI.Windowing.AppWindow, Microsoft.Windows.SDK.NET")
                            ?? Type.GetType("Microsoft.UI.Windowing.AppWindow, Microsoft.UI.Windowing");
        var getFromId = appWindowType?.GetMethod(
            "GetFromWindowId",
            BindingFlags.Public | BindingFlags.Static);
        var appWindow = getFromId?.Invoke(null, [windowId]);
        if (appWindow is null) return;

        // 全屏优先走 AppWindowPresenterKind.FullScreen
        if (fullscreen)
        {
            var kindType = Type.GetType("Microsoft.UI.Windowing.AppWindowPresenterKind, Microsoft.Windows.SDK.NET")
                           ?? Type.GetType("Microsoft.UI.Windowing.AppWindowPresenterKind, Microsoft.UI.Windowing");
            if (kindType is not null)
            {
                var fullScreen = Enum.Parse(kindType, "FullScreen");
                var setPresenter = appWindowType?.GetMethod("SetPresenter", [kindType]);
                setPresenter?.Invoke(appWindow, [fullScreen]);
                return;
            }
        }

        // 回到重叠窗再 Maximize / Restore
        var overlappedKindType = Type.GetType("Microsoft.UI.Windowing.AppWindowPresenterKind, Microsoft.Windows.SDK.NET")
                                 ?? Type.GetType("Microsoft.UI.Windowing.AppWindowPresenterKind, Microsoft.UI.Windowing");
        if (overlappedKindType is not null)
        {
            var overlapped = Enum.Parse(overlappedKindType, "Overlapped");
            appWindowType?.GetMethod("SetPresenter", [overlappedKindType])
                ?.Invoke(appWindow, [overlapped]);
        }

        var presenter = appWindow.GetType().GetProperty("Presenter")?.GetValue(appWindow);
        if (presenter is null) return;

        var presenterType = presenter.GetType();
        var alwaysProp = presenterType.GetProperty("IsAlwaysOnTop");
        if (alwaysProp?.CanWrite == true)
            alwaysProp.SetValue(presenter, alwaysOnTop);

        var setBorder = presenterType.GetMethod("SetBorderAndTitleBar", [typeof(bool), typeof(bool)]);
        setBorder?.Invoke(presenter, [true, !maximized]);

        var maximize = presenterType.GetMethod("Maximize", Type.EmptyTypes);
        var restore = presenterType.GetMethod("Restore", Type.EmptyTypes);
        if (maximized)
            maximize?.Invoke(presenter, null);
        else
            restore?.Invoke(presenter, null);
    }
}
