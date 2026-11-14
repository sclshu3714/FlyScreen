using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.ColorPicker.Ui.Desktop;

/// <summary>全屏透明层滴管：移动预览、左键锁定、右键/Esc 取消。</summary>
public sealed class DesktopEyedropperSession : IDisposable
{
    private readonly Window _overlay;
    private readonly IScreenColorSampler _sampler;
    private readonly Action<RgbaColor> _onMove;
    private readonly Action<RgbaColor> _onCommit;
    private readonly Action _onCancel;
    private bool _disposed;
    private bool _sampling;
    private long _lastMoveTick;

    public DesktopEyedropperSession(
        Window owner,
        IScreenColorSampler sampler,
        Action<RgbaColor> onMove,
        Action<RgbaColor> onCommit,
        Action onCancel)
    {
        _sampler = sampler;
        _onMove = onMove;
        _onCommit = onCommit;
        _onCancel = onCancel;

        _overlay = new Window
        {
            Title = "eyedropper",
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Cursor = new Cursor(StandardCursorType.Cross),
            WindowState = WindowState.FullScreen
        };
        _overlay.PointerMoved += OnMoved;
        _overlay.PointerPressed += OnPressed;
        _overlay.KeyDown += OnKey;
        _overlay.Show(owner);
        _overlay.Focus();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_disposed || _sampling) return;
        var now = Environment.TickCount64;
        if (now - _lastMoveTick < 33) return;
        _lastMoveTick = now;
        var p = e.GetPosition(null);
        // 屏幕坐标：叠加层为全屏，客户区坐标约等于屏幕
        var screen = _overlay.PointToScreen(p);
        SampleAt((int)screen.X, (int)screen.Y, commit: false, cancel: false);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed) return;
        var props = e.GetCurrentPoint(_overlay).Properties;
        var p = e.GetPosition(null);
        var screen = _overlay.PointToScreen(p);
        if (props.IsLeftButtonPressed)
            SampleAt((int)screen.X, (int)screen.Y, commit: true, cancel: false);
        else if (props.IsRightButtonPressed)
        {
            _onCancel();
            Dispose();
        }

        e.Handled = true;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _onCancel();
            Dispose();
            e.Handled = true;
        }
    }

    private void SampleAt(int x, int y, bool commit, bool cancel)
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            // 短暂隐藏叠加层，避免采到自身
            _overlay.Opacity = 0;
            Dispatcher.UIThread.RunJobs();
            if (_sampler.TrySample(x, y, out var color))
            {
                if (commit)
                {
                    _onCommit(color);
                    Dispose();
                    return;
                }

                if (!cancel)
                    _onMove(color);
            }
        }
        finally
        {
            if (!_disposed)
                _overlay.Opacity = 1;
            _sampling = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _overlay.Close(); }
        catch { /* ignore */ }
    }
}

