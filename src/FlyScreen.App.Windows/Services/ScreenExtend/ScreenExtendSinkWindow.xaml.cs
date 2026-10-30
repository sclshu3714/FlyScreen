using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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
        WindowState = WindowState.Maximized;
        Topmost = true;
        _cursorIdle.BeginSession();
        Cursor = System.Windows.Input.Cursors.None;
        Show();
        Activate();
        Focus();
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
