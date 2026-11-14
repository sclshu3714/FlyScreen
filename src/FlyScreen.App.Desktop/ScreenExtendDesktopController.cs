using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, LatestFrameSlot> _framesByPeer =
        new(StringComparer.OrdinalIgnoreCase);
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

    public Func<IReadOnlyList<string>>? ListAddablePeers { get; set; }
    public Func<string, Task>? StartExtraProjection { get; set; }

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

    private void EnsureSinkWindow()
    {
        _sink ??= new ScreenExtendSinkWindow(
            peer => _ = _service.StopSessionAsync(peer, "sink-close"),
            () => _ = _service.StopAsync("sink-close"),
            () => ListAddablePeers?.Invoke() ?? [],
            remote => StartExtraProjection?.Invoke(remote) ?? Task.CompletedTask);
    }

    private void OnSinkReady(string peer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                EnsureSinkWindow();
                _sink!.AddOrActivateTab(peer, _service);
                _sink.ShowFullscreen();
            }
            catch { /* ignore */ }
        });
    }

    private void OnFrame(ReadOnlyMemory<byte> jpeg, int w, int h, long capturedUnixMs, string peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return;
        var slot = _framesByPeer.GetOrAdd(peer.Trim(), _ => new LatestFrameSlot());
        if (!slot.Offer(jpeg, w, h, capturedUnixMs, peer.Trim())) return;
        KickDecode();
    }

    private void KickDecode()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _decodePosted, 1, 0) != 0) return;
        Task.Run(DecodeLoop);
    }

    private bool AnyFramePending()
    {
        foreach (var slot in _framesByPeer.Values)
        {
            if (slot.HasPending) return true;
        }

        return false;
    }

    private void DecodeLoop()
    {
        try
        {
            while (!_disposed)
            {
                var progressed = false;
                foreach (var kv in _framesByPeer.ToArray())
                {
                    var slot = kv.Value;
                    var peerKey = kv.Key;
                    while (!_disposed && slot.TryTake(out var jpeg, out _, out _, out var capturedAt, out var gen, out _))
                    {
                        progressed = true;
                        var src = ScreenExtendSinkWindow.DecodeJpeg(jpeg);
                        if (src == null || !slot.IsCurrent(gen))
                        {
                            src?.Dispose();
                            continue;
                        }

                        var shown = src;
                        var g = gen;
                        var capturedUnixMs = capturedAt;
                        var peer = peerKey;
                        var frameSlot = slot;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_disposed || _service.Role != ScreenExtendRole.Sink || !frameSlot.IsCurrent(g))
                            {
                                shown.Dispose();
                                return;
                            }

                            try
                            {
                                EnsureSinkWindow();
                                _sink!.EnsureTab(peer, _service);
                                if (!_sink.IsVisible)
                                    _sink.ShowFullscreen();
                                _sink.Present(peer, shown, capturedUnixMs);
                            }
                            catch
                            {
                                shown.Dispose();
                            }
                        });
                    }
                }

                if (!progressed) break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _decodePosted, 0);
            if (!_disposed && AnyFramePending())
                KickDecode();
        }
    }

    private void OnSessionEnded(string? peer)
    {
        if (!string.IsNullOrWhiteSpace(peer)
            && _framesByPeer.TryRemove(peer.Trim(), out var removed))
            removed.Clear();
        if (string.IsNullOrWhiteSpace(peer))
        {
            foreach (var s in _framesByPeer.Values) s.Clear();
            _framesByPeer.Clear();
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(peer))
                    _sink?.RemoveTab(peer);

                if (_sink != null && !_sink.HasTabs)
                {
                    _sink.CloseFromSession();
                    _sink = null;
                }
            }
            catch { /* ignore */ }
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
