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

    public ScreenExtendSinkWindow() : this(() => { }) { }

    public ScreenExtendSinkWindow(Action onUserClose)
    {
        _onUserClose = onUserClose;
        InitializeComponent();
        KeyDown += OnKeyDown;
        PointerMoved += (_, _) => _cursorIdle.NotifyLocalMouseMoved();
        Closing += (_, _) => NotifyUserClose();
        Closed += (_, _) =>
        {
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
        }
    }

    public void ShowFullscreen()
    {
        WindowState = WindowState.FullScreen;
        Topmost = true;
        _cursorIdle.BeginSession();
        Cursor = new Cursor(StandardCursorType.None);
        Show();
        Activate();
        Focus();
    }

    public void PresentJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            var bmp = new Bitmap(ms);
            var old = _current;
            _current = bmp;
            FrameImage.Source = bmp;
            WaitingText.IsVisible = false;
            old?.Dispose();
        }
        catch { /* ignore */ }
    }

    public void CloseFromSession()
    {
        _closingFromSession = true;
        try { Close(); } catch { /* ignore */ }
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
