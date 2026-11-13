using FlyScreen.Mac.Input;
using FlyScreen.Mac.Native;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Mac.ScreenExtend;

/// <summary>
/// Mac 投影主机：复制抓主屏；扩展抓第二块屏，没有就建虚拟副屏。
/// 帧仍是 JPEG，从机不用改。
/// </summary>
public sealed class MacProjectionHost : IDisposable
{
    private readonly ScreenExtendService _service;
    private readonly MacInputInjector _injector;
    private readonly object _gate = new();
    private DisplayProjectionMode _mode = DisplayProjectionMode.Clone;
    private ProjectionDisplayChoice _choice = new();
    private LayoutEdgeSide _side = LayoutEdgeSide.Right;
    private MacVirtualDisplay? _virtual;
    private uint _extendDisplayId;
    private CancellationTokenSource? _captureCts;
    private MacDisplayInfo? _capture;
    private long _hostReadyTick;
    private int _remoteDriving;
    private bool _disposed;

    public MacProjectionHost(ScreenExtendService service, MacInputInjector injector)
    {
        _service = service;
        _injector = injector;
        _service.HostSessionReady += OnHostReady;
        _service.SessionEnded += OnSessionEnded;
        _service.ReverseInputReceived += OnReverseInput;
    }

    public void Configure(DisplayProjectionMode mode, ProjectionDisplayChoice choice, LayoutEdgeSide side)
    {
        lock (_gate)
        {
            _mode = mode;
            _choice = choice.Clone();
            if (side != LayoutEdgeSide.Unknown)
                _side = side;
        }
    }

    public (bool Ok, string? Error) PrepareExtend()
    {
        ReleaseExtend();
        var existing = MacDisplays.TrySecondary();
        if (existing != null)
        {
            _extendDisplayId = existing.Value.Id;
            return (true, $"第二块屏幕 {existing.Value.PixelWidth}×{existing.Value.PixelHeight}");
        }

        if (!MacVirtualDisplay.IsSupported)
            return (false, "没有第二块屏幕，并且当前系统不能创建虚拟副屏。");

        var (w, h) = DesiredExtendPixels();
        var created = new MacVirtualDisplay();
        if (!created.TryCreate(w, h, 60, out var error))
        {
            created.Dispose();
            return (false, error ?? "无法创建虚拟副屏");
        }

        _virtual = created;
        _extendDisplayId = created.DisplayId;
        Thread.Sleep(350);
        var anchor = MacDisplays.TryPrimary();
        if (anchor != null)
            MacDisplays.TryPlace(created.DisplayId, anchor.Value, w, h, _side);
        return (true, $"虚拟副屏 {w}×{h}");
    }

    public void ReleaseExtend()
    {
        _extendDisplayId = 0;
        var virtualDisplay = _virtual;
        _virtual = null;
        virtualDisplay?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.HostSessionReady -= OnHostReady;
        _service.SessionEnded -= OnSessionEnded;
        _service.ReverseInputReceived -= OnReverseInput;
        StopCapture();
        ReleaseExtend();
    }

    private void OnHostReady()
    {
        _hostReadyTick = Environment.TickCount64;
        Interlocked.Exchange(ref _remoteDriving, 0);
        if (_mode == DisplayProjectionMode.Extend)
            TryMatchPeerSize();
        StartCapture();
    }

    private void OnSessionEnded(string? peer)
    {
        StopCapture();
        ReleaseExtend();
        Interlocked.Exchange(ref _remoteDriving, 0);
    }

    private void TryMatchPeerSize()
    {
        if (_virtual == null) return;
        if (_choice.Source != ProjectionDisplaySource.TargetPeer) return;
        if (_service.PeerSinkWidth < 640 || _service.PeerSinkHeight < 480) return;
        var (w, h) = DesiredExtendPixels();
        if (Math.Abs(w - _service.PeerSinkWidth) < 8 && Math.Abs(h - _service.PeerSinkHeight) < 8)
            return;

        var side = _side;
        ReleaseExtend();
        var created = new MacVirtualDisplay();
        if (!created.TryCreate(_service.PeerSinkWidth, _service.PeerSinkHeight, 60, out _))
        {
            created.Dispose();
            if (!PrepareExtend().Ok)
                return;
            return;
        }

        _virtual = created;
        _extendDisplayId = created.DisplayId;
        _side = side;
        Thread.Sleep(250);
        var anchor = MacDisplays.TryPrimary();
        if (anchor != null)
            MacDisplays.TryPlace(created.DisplayId, anchor.Value, _service.PeerSinkWidth, _service.PeerSinkHeight, side);
    }

    private (int W, int H) DesiredExtendPixels()
    {
        if (_choice.Source == ProjectionDisplaySource.TargetPeer
            && _service.PeerSinkWidth >= 640 && _service.PeerSinkHeight >= 480)
            return (_service.PeerSinkWidth, _service.PeerSinkHeight);

        if (_choice.Width >= 640 && _choice.Height >= 480)
            return (_choice.Width, _choice.Height);

        var primary = MacDisplays.TryPrimary();
        if (primary != null)
            return (primary.Value.PixelWidth, primary.Value.PixelHeight);
        return (1920, 1080);
    }

    private void StartCapture()
    {
        StopCapture();
        var cts = new CancellationTokenSource();
        _captureCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _service.Role == ScreenExtendRole.Host)
            {
                var display = ResolveCaptureDisplay();
                if (display == null)
                {
                    try { await Task.Delay(200, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _capture = display;
                var choice = _choice;
                var picture = ProjectionDisplayPresets.DescribeQuality(choice.JpegQuality);
                var maxEdge = picture.Passthrough ? 0 : Math.Max(choice.Width, choice.Height);
                if (!picture.Passthrough && picture.ExtraLongEdgeCap > 0)
                    maxEdge = maxEdge > 0 ? Math.Min(maxEdge, picture.ExtraLongEdgeCap) : picture.ExtraLongEdgeCap;
                var interval = ProjectionDisplayPresets.FpsToIntervalMs(choice.Fps);
                var started = Environment.TickCount64;
                try
                {
                    var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var drawCursor = Volatile.Read(ref _remoteDriving) == 0;
                    var frame = MacScreenCapture.Capture(display.Value.Id, picture.EncodeQuality, maxEdge, drawCursor);
                    if (frame is { Jpeg.Length: > 0 })
                    {
                        await _service.SendFrameAsync(frame.Value.Jpeg, frame.Value.Width, frame.Value.Height,
                                capturedAt, token)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                    /* 单帧失败继续 */
                }

                var wait = interval - (int)(Environment.TickCount64 - started);
                if (wait <= 0) continue;
                try { await Task.Delay(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private void StopCapture()
    {
        var cts = _captureCts;
        _captureCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
    }

    private MacDisplayInfo? ResolveCaptureDisplay()
    {
        if (_mode == DisplayProjectionMode.Clone)
            return MacDisplays.TryPrimary();

        if (_extendDisplayId != 0)
        {
            foreach (var d in MacDisplays.List())
            {
                if (d.Id == _extendDisplayId)
                    return d;
            }

            var primary = MacDisplays.TryPrimary();
            if (primary != null && primary.Value.Id != _extendDisplayId)
            {
                return new MacDisplayInfo(
                    _extendDisplayId,
                    false,
                    primary.Value.X + primary.Value.PointWidth,
                    primary.Value.Y,
                    Math.Max(1, _choice.Width),
                    Math.Max(1, _choice.Height),
                    Math.Max(1, _choice.Width),
                    Math.Max(1, _choice.Height),
                    primary.Value.ScalePercent);
            }
        }

        return MacDisplays.TrySecondary();
    }

    private void OnReverseInput(ScreenExtendInputPayload input)
    {
        try
        {
            var display = _capture ?? ResolveCaptureDisplay();
            if (display == null) return;
            if (input.Kind == ScreenExtendInputPayload.KindMove
                && _hostReadyTick != 0
                && Environment.TickCount64 - _hostReadyTick < 500
                && input.Nx <= 0.03 && input.Ny <= 0.03)
                return;

            if (input.Kind is ScreenExtendInputPayload.KindMove
                or ScreenExtendInputPayload.KindButton
                or ScreenExtendInputPayload.KindWheel)
                Interlocked.Exchange(ref _remoteDriving, 1);

            var d = display.Value;
            var nx = Math.Clamp(input.Nx, 0, 1);
            var ny = Math.Clamp(input.Ny, 0, 1);
            var x = d.X + nx * Math.Max(1, d.PointWidth - 1);
            var y = d.Y + (1 - ny) * Math.Max(1, d.PointHeight - 1);

            switch (input.Kind)
            {
                case ScreenExtendInputPayload.KindMove:
                    _injector.MoveAbsoluteGlobal(x, y);
                    break;
                case ScreenExtendInputPayload.KindButton:
                    _injector.MoveAbsoluteGlobal(x, y);
                    _injector.MouseButton(new MouseButtonPayload { Button = input.Button, Down = input.Down });
                    break;
                case ScreenExtendInputPayload.KindWheel:
                    _injector.MoveAbsoluteGlobal(x, y);
                    _injector.MouseWheel(new MouseWheelPayload { Delta = input.Delta, Horizontal = input.Horizontal });
                    break;
                case ScreenExtendInputPayload.KindKey:
                    if (!string.IsNullOrEmpty(input.Text))
                        _injector.Key(new KeyEventPayload { Text = input.Text, Down = input.Down });
                    else
                        _injector.Key(new KeyEventPayload
                        {
                            VirtualKey = input.VirtualKey,
                            Down = input.Down,
                            Extended = input.Extended
                        });
                    break;
            }
        }
        catch
        {
            /* 注入失败不影响推流 */
        }
    }
}
