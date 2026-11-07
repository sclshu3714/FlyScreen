using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyScreen.Native;

namespace FlyScreen.Services.ScreenExtend;

public partial class ScreenExtendSinkWindow : Window
{
    private readonly Action _onCloseRequest;
    private readonly SinkLocalCursorIdleHide _cursorIdle = new();
    private readonly ScreenExtendReverseMoveGate _reverseGate = new();
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private ScreenExtendService? _session;
    private bool _captureBound;
    private int _contentW;
    private int _contentH;

    public ScreenExtendSinkWindow(Action onCloseRequest)
    {
        _onCloseRequest = onCloseRequest;
        InitializeComponent();
        // Closing：Alt+F4 / 关窗尽早通知主机；Closed 兜底（异常路径）
        Closing += (_, _) => NotifyUserClose();
        Closed += (_, _) =>
        {
            DetachCapture();
            _cursorIdle.EndSession();
            _cursorIdle.Dispose();
            NotifyUserClose();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Activated += (_, _) =>
        {
            Focus();
            TryAttachCapture();
        };
        Deactivated += (_, _) => DetachCapture();
        MouseMove += (_, _) => _cursorIdle.NotifyLocalMouseMoved();
        _cursorIdle.ShowLocalCursorChanged += show =>
        {
            Dispatcher.BeginInvoke(() =>
                Cursor = show ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.None);
        };
    }

    public void BindSession(ScreenExtendService service)
    {
        _session = service;
        if (IsActive)
            TryAttachCapture();
        else
            DetachCapture();
    }

    private void TryAttachCapture()
    {
        if (_captureBound) return;
        if (_session?.CanSendReverseInput != true) return;
        _captureBound = true;
        _reverseGate.OnCaptureAttached();
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseUp += OnPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    private void DetachCapture()
    {
        if (!_captureBound) return;
        _captureBound = false;
        _reverseGate.OnCaptureDetached();
        PreviewMouseMove -= OnPreviewMouseMove;
        PreviewMouseDown -= OnPreviewMouseDown;
        PreviewMouseUp -= OnPreviewMouseUp;
        PreviewMouseWheel -= OnPreviewMouseWheel;
        PreviewKeyDown -= OnPreviewKeyDown;
        PreviewKeyUp -= OnPreviewKeyUp;
    }

    private bool TryMap(MouseEventArgs e, out double nx, out double ny)
    {
        var p = e.GetPosition(this);
        return ScreenExtendReverseInput.TryMapUniformContent(
            ActualWidth, ActualHeight, _contentW, _contentH, p.X, p.Y, out nx, out ny);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendMove(nx, ny)) return;
        _ = _session.SendReverseMoveAsync(nx, ny);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e.ChangedButton);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: true);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e.ChangedButton);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: false);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        e.Handled = true;
        _ = _session.SendReverseWheelAsync(nx, ny, e.Delta, horizontal: false);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return; // Window_KeyDown 停投影
        if (_session?.CanSendReverseInput != true) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: true, extended: IsExtended(key));
    }

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return;
        if (_session?.CanSendReverseInput != true) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: false, extended: IsExtended(key));
    }

    private static int MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => -1
    };

    private static bool IsExtended(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.NumLock
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;

    private void NotifyUserClose()
    {
        if (_closingFromSession || _userCloseRaised) return;
        _userCloseRaised = true;
        try { _onCloseRequest(); } catch { /* ignore */ }
    }

    public void CloseFromSession()
    {
        _closingFromSession = true;
        DetachCapture();
        try
        {
            Topmost = false;
            WindowState = WindowState.Normal;
            Hide();
            Close();
        }
        catch { /* ignore */ }
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
        TryAttachCapture();
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

    public readonly record struct DecodedFrame(int Width, int Height, byte[] Bgra, int Stride);

    /// <summary>在后台线程解 JPEG。界面线程只拷像素，避免解码把画面卡顿。</summary>
    public static DecodedFrame? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var bmp = new System.Drawing.Bitmap(ms);
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var stride = data.Stride;
                var abs = Math.Abs(stride);
                var bytes = new byte[abs * bmp.Height];
                if (stride >= 0)
                {
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                }
                else
                {
                    for (var y = 0; y < bmp.Height; y++)
                        Marshal.Copy(data.Scan0 + stride * (bmp.Height - 1 - y), bytes, y * abs, abs);
                }

                return new DecodedFrame(bmp.Width, bmp.Height, bytes, abs);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    private WriteableBitmap? _frameBitmap;

    public void Present(DecodedFrame frame)
    {
        _contentW = frame.Width;
        _contentH = frame.Height;
        if (_frameBitmap == null
            || _frameBitmap.PixelWidth != frame.Width
            || _frameBitmap.PixelHeight != frame.Height)
        {
            _frameBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            FrameImage.Source = _frameBitmap;
        }

        _frameBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
        if (WaitingText.Visibility != Visibility.Collapsed)
            WaitingText.Visibility = Visibility.Collapsed;
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
