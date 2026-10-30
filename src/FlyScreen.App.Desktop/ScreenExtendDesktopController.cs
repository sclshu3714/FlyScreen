using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.DesktopUi;

/// <summary>Desktop：从机全屏收流（须在网络启动前挂好，避免 Accept 后无窗口）。</summary>
public sealed class ScreenExtendDesktopController : IDisposable
{
    private readonly ScreenExtendService _service;
    private ScreenExtendSinkWindow? _sink;
    private bool _disposed;

    public ScreenExtendDesktopController(ScreenExtendService service)
    {
        _service = service;
        _service.ShouldAcceptOffer = (_, _) => true;
        _service.GetSinkDisplaySize = ReadPrimarySize;
        _service.SinkSessionReady += OnSinkReady;
        _service.SessionEnded += OnSessionEnded;
        _service.FrameReceived += OnFrame;
    }

    private static (int Width, int Height) ReadPrimarySize()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            {
                var win = desk.MainWindow;
                var screen = win?.Screens?.Primary ?? win?.Screens?.All.FirstOrDefault();
                if (screen != null)
                {
                    var b = screen.Bounds;
                    var w = Math.Max(1, b.Width);
                    var h = Math.Max(1, b.Height);
                    return (w, h);
                }
            }
        }
        catch { /* ignore */ }

        return (1920, 1080);
    }

    private void OnSinkReady()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _sink ??= new ScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
                _sink.ShowFullscreen();
            }
            catch { /* ignore */ }
        });
    }

    private void OnFrame(ReadOnlyMemory<byte> jpeg, int w, int h)
    {
        var copy = jpeg.ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _sink ??= new ScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
                if (!_sink.IsVisible)
                    _sink.ShowFullscreen();
                _sink.PresentJpeg(copy);
            }
            catch { /* ignore */ }
        });
    }

    private void OnSessionEnded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { _sink?.CloseFromSession(); } catch { /* ignore */ }
            _sink = null;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.SinkSessionReady -= OnSinkReady;
        _service.SessionEnded -= OnSessionEnded;
        _service.FrameReceived -= OnFrame;
        try
        {
            if (_service.IsActive)
                _service.StopAsync("app-exit").GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
        try { _sink?.CloseFromSession(); } catch { /* ignore */ }
        _sink = null;
    }
}
