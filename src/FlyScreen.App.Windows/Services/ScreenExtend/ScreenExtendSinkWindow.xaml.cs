using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using FlyScreen.Native;

namespace FlyScreen.Services.ScreenExtend;

public partial class ScreenExtendSinkWindow : Window
{
    private readonly Action _onCloseRequest;
    private readonly SinkLocalCursorIdleHide _cursorIdle = new();
    private bool _closingFromSession;
    private bool _userCloseRaised;

    public ScreenExtendSinkWindow(Action onCloseRequest)
    {
        _onCloseRequest = onCloseRequest;
        InitializeComponent();
        // Closing：Alt+F4 / 关窗尽早通知主机；Closed 兜底（异常路径）
        Closing += (_, _) => NotifyUserClose();
        Closed += (_, _) =>
        {
            _cursorIdle.EndSession();
            _cursorIdle.Dispose();
            NotifyUserClose();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Activated += (_, _) => Focus();
        MouseMove += (_, _) => _cursorIdle.NotifyLocalMouseMoved();
        _cursorIdle.ShowLocalCursorChanged += show =>
        {
            Dispatcher.BeginInvoke(() =>
                Cursor = show ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.None);
        };
    }

    private void NotifyUserClose()
    {
        if (_closingFromSession || _userCloseRaised) return;
        _userCloseRaised = true;
        try { _onCloseRequest(); } catch { /* ignore */ }
    }

    public void CloseFromSession()
    {
        _closingFromSession = true;
        try { Close(); } catch { /* ignore */ }
    }

    public void ShowFullscreen()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        _cursorIdle.BeginSession();
        Cursor = System.Windows.Input.Cursors.None;
        if (!IsVisible)
            Show();
        WindowState = WindowState.Maximized;
        Activate();
        Focus();
    }

    /// <summary>无边框最大化默认停在工作区。把最大尺寸设成整块显示器，画面才能贴到屏幕边缘。</summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        if (msg != wmGetMinMaxInfo)
            return IntPtr.Zero;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var rc = info.rcMonitor;
        var work = info.rcWork;
        mmi.MaxPosition.X = rc.Left - work.Left;
        mmi.MaxPosition.Y = rc.Top - work.Top;
        mmi.MaxSize.X = rc.Right - rc.Left;
        mmi.MaxSize.Y = rc.Bottom - rc.Top;
        mmi.MaxTrackSize = mmi.MaxSize;
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativeMethods.POINT Reserved;
        public NativeMethods.POINT MaxSize;
        public NativeMethods.POINT MaxPosition;
        public NativeMethods.POINT MinTrackSize;
        public NativeMethods.POINT MaxTrackSize;
    }

    public void PresentJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            FrameImage.Source = bmp;
            if (WaitingText.Visibility != Visibility.Collapsed)
                WaitingText.Visibility = Visibility.Collapsed;
        }
        catch { /* ignore bad frame */ }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NotifyUserClose();
            Close();
        }
    }
}
