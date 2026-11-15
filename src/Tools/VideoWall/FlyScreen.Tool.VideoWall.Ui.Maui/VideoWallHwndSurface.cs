using System.Runtime.InteropServices;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Maui-Win 视频表面：在顶层窗口下挂 Win32 子 HWND，供桌面版 LibVLC <c>MediaPlayer.Hwnd</c> 嵌入。
/// </summary>
public sealed class VideoWallHwndSurface : Border
{
#if WINDOWS
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsExTransparent = 0x00000020;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int SwpHideWindow = 0x0080;

    private IntPtr _hwnd;
    private bool _bound;
    private bool _platformHooks;
    private bool _surfaceShown = true;
#endif

    public IntPtr Hwnd
    {
        get
        {
#if WINDOWS
            return _hwnd;
#else
            return IntPtr.Zero;
#endif
        }
    }

    public event Action? HwndReady;

    public VideoWallHwndSurface()
    {
        BackgroundColor = Colors.Black;
        StrokeThickness = 0;
        Stroke = Colors.Transparent;
#if WINDOWS
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => SyncBounds();
        HandlerChanged += (_, _) => EnsurePlatformHooks();
#endif
    }

#if WINDOWS
    private void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            EnsurePlatformHooks();
            EnsureChildHwnd();
            SyncBounds();
            if (_hwnd != IntPtr.Zero)
                HwndReady?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VideoWallHwndSurface.OnLoaded: {ex.Message}");
        }
    }

    private void OnUnloaded(object? sender, EventArgs e) => DestroyChildHwnd();

    private void EnsurePlatformHooks()
    {
        if (_platformHooks) return;
        if (Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement fe)
            return;
        fe.SizeChanged += (_, _) => SyncBounds();
        fe.LayoutUpdated += (_, _) => SyncBounds();
        _platformHooks = true;
    }

    private void EnsureChildHwnd()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        var parent = ResolveParentHwnd();
        if (parent == IntPtr.Zero)
            return;

        _hwnd = CreateWindowEx(
            WsExTransparent,
            "Static",
            "",
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 1, 1,
            parent,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        _bound = _hwnd != IntPtr.Zero;
    }

    private IntPtr ResolveParentHwnd()
    {
        try
        {
            var hwnd = TryGetHwndFromWindow(Window);
            if (hwnd != IntPtr.Zero)
                return hwnd;
        }
        catch { /* ignore */ }

        foreach (var w in Application.Current?.Windows ?? [])
        {
            var hwnd = TryGetHwndFromWindow(w);
            if (hwnd != IntPtr.Zero)
                return hwnd;
        }

        return IntPtr.Zero;
    }

    private static IntPtr TryGetHwndFromWindow(Window? window)
    {
        if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
            return IntPtr.Zero;
        return WinRT.Interop.WindowNative.GetWindowHandle(native);
    }

    private void SyncBounds()
    {
        if (_hwnd == IntPtr.Zero)
        {
            if (_surfaceShown)
                EnsureChildHwnd();
            if (_hwnd == IntPtr.Zero)
                return;
        }

        if (Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement fe)
            return;
        if (fe.XamlRoot is null || fe.ActualWidth <= 1 || fe.ActualHeight <= 1)
        {
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpHideWindow);
            return;
        }

        try
        {
            var transform = fe.TransformToVisual(null);
            var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            var scale = fe.XamlRoot.RasterizationScale;
            var x = (int)Math.Round(topLeft.X * scale);
            var y = (int)Math.Round(topLeft.Y * scale);
            var w = Math.Max(1, (int)Math.Round(fe.ActualWidth * scale));
            var h = Math.Max(1, (int)Math.Round(fe.ActualHeight * scale));
            var visible = _surfaceShown && IsVisible && Opacity > 0.01;
            var flags = SwpNoZOrder | SwpNoActivate | (visible ? SwpShowWindow : SwpHideWindow);
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, flags);
        }
        catch
        {
            /* Transform 可能在卸载瞬间失效 */
        }
    }

    private void DestroyChildHwnd()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _bound = false;
    }

    public void SetSurfaceVisible(bool visible)
    {
        _surfaceShown = visible;
        if (visible)
        {
            EnsurePlatformHooks();
            EnsureChildHwnd();
        }

        SyncBounds();
        if (visible && _hwnd != IntPtr.Zero)
            HwndReady?.Invoke();
    }

    public void Sync() => SyncBounds();

    /// <summary>等待布局给出有效尺寸后再 Sync，避免进播放页时 HWND 仍是 0×0（需拖窗口才出画）。</summary>
    public async Task<bool> WaitUntilLaidOutAsync(int timeoutMs = 2000)
    {
        EnsurePlatformHooks();
        EnsureChildHwnd();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            SyncBounds();
            if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe
                && fe.ActualWidth > 1 && fe.ActualHeight > 1
                && _hwnd != IntPtr.Zero)
                return true;
            await Task.Delay(32).ConfigureAwait(true);
        }

        SyncBounds();
        return Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe2
               && fe2.ActualWidth > 1 && fe2.ActualHeight > 1
               && _hwnd != IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
#else
    public void SetSurfaceVisible(bool visible) => IsVisible = visible;
    public void Sync() { }
    public Task<bool> WaitUntilLaidOutAsync(int timeoutMs = 2000) => Task.FromResult(true);
#endif
}
