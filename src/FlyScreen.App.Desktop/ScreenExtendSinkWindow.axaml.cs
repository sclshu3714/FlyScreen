using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.DesktopUi;

public partial class ScreenExtendSinkWindow : Window
{
    private readonly Action _onUserClose;
    private readonly SinkLocalCursorIdleHide _cursorIdle = new();
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private Bitmap? _current;
    private ScreenExtendService? _session;
    private bool _captureBound;
    private int _contentW;
    private int _contentH;

    public ScreenExtendSinkWindow() : this(() => { }) { }

    public ScreenExtendSinkWindow(Action onUserClose)
    {
        _onUserClose = onUserClose;
        InitializeComponent();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        PointerMoved += (_, _) => _cursorIdle.NotifyLocalMouseMoved();
        Activated += (_, _) =>
        {
            Focus();
            TryAttachCapture();
        };
        Deactivated += (_, _) => DetachCapture();
        Closing += (_, _) => NotifyUserClose();
        Closed += (_, _) =>
        {
            DetachCapture();
            _cursorIdle.EndSession();
            _cursorIdle.Dispose();
            DisposeBitmap();
            NotifyUserClose();
        };
        _cursorIdle.ShowLocalCursorChanged += show =>
        {
            Dispatcher.UIThread.Post(() =>
                Cursor = show ? new Cursor(StandardCursorType.Arrow) : new Cursor(StandardCursorType.None));
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
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;
    }

    private void DetachCapture()
    {
        if (!_captureBound) return;
        _captureBound = false;
        PointerMoved -= OnPointerMoved;
        PointerPressed -= OnPointerPressed;
        PointerReleased -= OnPointerReleased;
        PointerWheelChanged -= OnPointerWheel;
    }

    private bool TryMap(PointerEventArgs e, out double nx, out double ny)
    {
        var p = e.GetPosition(this);
        return ScreenExtendReverseInput.TryMapUniformContent(
            Bounds.Width, Bounds.Height, _contentW, _contentH, p.X, p.Y, out nx, out ny);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        _ = _session.SendReverseMoveAsync(nx, ny);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        var button = MapButton(e);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: true);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        var button = MapButton(e);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: false);
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        var delta = (int)Math.Round(e.Delta.Y * 120);
        if (delta == 0)
            delta = (int)Math.Round(e.Delta.X * 120);
        if (delta == 0) return;
        e.Handled = true;
        var horizontal = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        _ = _session.SendReverseWheelAsync(nx, ny, delta, horizontal);
    }

    private static int MapButton(PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (props.IsLeftButtonPressed || props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            || props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased)
            return 0;
        if (props.IsRightButtonPressed || props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
            || props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
            return 1;
        if (props.IsMiddleButtonPressed || props.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed
            || props.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            return 2;
        return -1;
    }

    private void NotifyUserClose()
    {
        if (_closingFromSession || _userCloseRaised) return;
        _userCloseRaised = true;
        try { _onUserClose(); } catch { /* ignore */ }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NotifyUserClose();
            Close();
            return;
        }

        if (_session?.CanSendReverseInput != true) return;
        var vk = ToWindowsVk(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: true, extended: IsExtended(e.Key));
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return;
        if (_session?.CanSendReverseInput != true) return;
        var vk = ToWindowsVk(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: false, extended: IsExtended(e.Key));
    }

    /// <summary>Avalonia Key → Windows VK（主机注入用）。</summary>
    private static ushort ToWindowsVk(Key key) => key switch
    {
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Return => 0x0D,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.PrintScreen => 0x2C,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.D0 => 0x30,
        Key.D1 => 0x31,
        Key.D2 => 0x32,
        Key.D3 => 0x33,
        Key.D4 => 0x34,
        Key.D5 => 0x35,
        Key.D6 => 0x36,
        Key.D7 => 0x37,
        Key.D8 => 0x38,
        Key.D9 => 0x39,
        Key.A => 0x41,
        Key.B => 0x42,
        Key.C => 0x43,
        Key.D => 0x44,
        Key.E => 0x45,
        Key.F => 0x46,
        Key.G => 0x47,
        Key.H => 0x48,
        Key.I => 0x49,
        Key.J => 0x4A,
        Key.K => 0x4B,
        Key.L => 0x4C,
        Key.M => 0x4D,
        Key.N => 0x4E,
        Key.O => 0x4F,
        Key.P => 0x50,
        Key.Q => 0x51,
        Key.R => 0x52,
        Key.S => 0x53,
        Key.T => 0x54,
        Key.U => 0x55,
        Key.V => 0x56,
        Key.W => 0x57,
        Key.X => 0x58,
        Key.Y => 0x59,
        Key.Z => 0x5A,
        Key.LWin => 0x5B,
        Key.RWin => 0x5C,
        Key.NumPad0 => 0x60,
        Key.NumPad1 => 0x61,
        Key.NumPad2 => 0x62,
        Key.NumPad3 => 0x63,
        Key.NumPad4 => 0x64,
        Key.NumPad5 => 0x65,
        Key.NumPad6 => 0x66,
        Key.NumPad7 => 0x67,
        Key.NumPad8 => 0x68,
        Key.NumPad9 => 0x69,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,
        Key.F1 => 0x70,
        Key.F2 => 0x71,
        Key.F3 => 0x72,
        Key.F4 => 0x73,
        Key.F5 => 0x74,
        Key.F6 => 0x75,
        Key.F7 => 0x76,
        Key.F8 => 0x77,
        Key.F9 => 0x78,
        Key.F10 => 0x79,
        Key.F11 => 0x7A,
        Key.F12 => 0x7B,
        Key.NumLock => 0x90,
        Key.Scroll => 0x91,
        Key.LeftShift => 0xA0,
        Key.RightShift => 0xA1,
        Key.LeftCtrl => 0xA2,
        Key.RightCtrl => 0xA3,
        Key.LeftAlt => 0xA4,
        Key.RightAlt => 0xA5,
        Key.OemSemicolon => 0xBA,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemMinus => 0xBD,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB,
        Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE,
        _ => 0
    };

    private static bool IsExtended(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.NumLock
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;

    public void ShowFullscreen()
    {
        WindowState = WindowState.FullScreen;
        Topmost = true;
        _cursorIdle.BeginSession();
        Cursor = new Cursor(StandardCursorType.None);
        Show();
        Activate();
        Focus();
        TryAttachCapture();
    }

    public static Bitmap? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public void Present(Bitmap bmp)
    {
        _contentW = bmp.PixelSize.Width;
        _contentH = bmp.PixelSize.Height;
        var old = _current;
        _current = bmp;
        FrameImage.Source = bmp;
        WaitingText.IsVisible = false;
        old?.Dispose();
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

    private void DisposeBitmap()
    {
        try
        {
            FrameImage.Source = null;
            _current?.Dispose();
            _current = null;
        }
        catch { /* ignore */ }
    }
}
