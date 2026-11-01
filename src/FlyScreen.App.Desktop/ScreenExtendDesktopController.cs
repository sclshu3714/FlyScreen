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
    private readonly LatestFrameSlot _frames = new();
    private int _decodePosted;

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
        if (!_frames.Offer(jpeg, w, h)) return;
        KickDecode();
    }

    private void KickDecode()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _decodePosted, 1, 0) != 0) return;
        Task.Run(DecodeLoop);
    }

    private void DecodeLoop()
    {
        try
        {
            while (!_disposed && _frames.TryTake(out var jpeg, out _, out _, out var gen))
            {
                var src = ScreenExtendSinkWindow.DecodeJpeg(jpeg);
                if (src == null || !_frames.IsCurrent(gen))
                {
                    src?.Dispose();
                    continue;
                }

                var shown = src;
                var g = gen;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || _service.Role != ScreenExtendRole.Sink || !_frames.IsCurrent(g))
                    {
                        shown.Dispose();
                        return;
                    }

                    try
                    {
                        _sink ??= new ScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
                        if (!_sink.IsVisible)
                            _sink.ShowFullscreen();
                        _sink.Present(shown);
                    }
                    catch
                    {
                        shown.Dispose();
                    }
                });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _decodePosted, 0);
            if (!_disposed && _frames.HasPending)
                KickDecode();
        }
    }

    private void OnSessionEnded()
    {
        _frames.Clear();
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
