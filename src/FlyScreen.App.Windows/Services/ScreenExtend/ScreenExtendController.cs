using System.Windows.Threading;
using FlyScreen.ScreenExtend;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>Win 主机推流 + 从机全屏窗绑定。</summary>
public sealed class ScreenExtendController : IDisposable
{
    private readonly ScreenExtendService _service;
    private readonly Dispatcher _dispatcher;
    private VirtualDisplaySession? _vdd;
    private CancellationTokenSource? _captureCts;
    private ScreenExtendSinkWindow? _sinkWindow;
    private DisplayProjectionMode _mode = DisplayProjectionMode.Extend;
    private LayoutEdgeSide _layoutSide = LayoutEdgeSide.Unknown;
    private ProjectionDisplayChoice _display = new();
    private bool _wantDisplay;
    private bool _hostSessionActive;
    private bool _disposed;

    public ScreenExtendController(ScreenExtendService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _service.HostSessionReady += OnHostReady;
        _service.SinkSessionReady += OnSinkReady;
        _service.SessionEnded += OnSessionEnded;
        _service.FrameReceived += OnFrame;
        _service.ShouldAcceptOffer = (_, _) => true;
        _service.GetSinkDisplayInfo = () =>
        {
            var info = DisplayModeHelper.ToDisplayInfoPayload();
            return (info.Width, info.Height, info.ScalePercent);
        };
    }

    public ScreenExtendService Service => _service;
    public DisplayProjectionMode Mode => _mode;
    public ProjectionDisplayChoice DisplayChoice => _display;

    public void SetProjectionMode(DisplayProjectionMode mode) => _mode = mode;

    public void SetDisplayChoice(ProjectionDisplayChoice? choice)
        => _display = choice?.Clone() ?? new ProjectionDisplayChoice();

    /// <summary>扩展/仅第二屏：准备第二输出。复制模式不强制虚拟屏。
    /// <paramref name="layoutSide"/>：对端在电脑布局中相对本机的方位，用于摆放虚拟屏。</summary>
    public async Task<(bool Ok, string? Error)> EnsureHostDisplayAsync(
        CancellationToken ct = default,
        LayoutEdgeSide layoutSide = LayoutEdgeSide.Unknown)
    {
        _wantDisplay = true;
        if (layoutSide != LayoutEdgeSide.Unknown)
            _layoutSide = layoutSide;

        var (sw, sh, srf) = ResolveSecondaryMode();
        if (_vdd != null)
        {
            var (okRepo, errRepo) = await _vdd.EnsureAsync(ct, layoutSide, sw, sh, srf).ConfigureAwait(false);
            if (!okRepo)
                return (false, errRepo);
            var tr = _vdd.Target!.Value;
            return (true, $"{tr.Width}x{tr.Height}" + (_vdd.UsedVirtualDriver ? "（虚拟）" : "（已有副屏）"));
        }

        var vdd = new VirtualDisplaySession();
        var (ok, err) = await vdd.EnsureAsync(ct, layoutSide, sw, sh, srf).ConfigureAwait(false);
        if (!ok)
        {
            await vdd.DisposeAsync().ConfigureAwait(false);
            return (false, err);
        }

        _vdd = vdd;
        var t = vdd.Target!.Value;
        return (true, $"{t.Width}x{t.Height}" + (vdd.UsedVirtualDriver ? "（虚拟）" : "（已有副屏）"));
    }

    private (int W, int H, int Refresh) ResolveSecondaryMode()
    {
        if (_display.Source == ProjectionDisplaySource.TargetPeer
            && _service.PeerSinkWidth > 0 && _service.PeerSinkHeight > 0)
            return (_service.PeerSinkWidth, _service.PeerSinkHeight,
                _display.RefreshHz > 0 ? _display.RefreshHz : 60);

        if (_display.Width > 0 && _display.Height > 0)
            return (_display.Width, _display.Height, _display.RefreshHz > 0 ? _display.RefreshHz : 60);

        var primary = DisplayModeHelper.GetPrimaryInfo();
        return (primary.Width, primary.Height, primary.RefreshHz);
    }

    public async Task ReleaseHostDisplayAsync()
    {
        _wantDisplay = false;
        await FinalizeAfterSessionEndedAsync(releaseHostDisplay: true).ConfigureAwait(false);
    }

    private void OnHostReady()
    {
        _hostSessionActive = true;
        _ = OnHostReadyAsync();
    }

    private async Task OnHostReadyAsync()
    {
        // Accept 后按所选分辨率（目标电脑自动则用 PeerSink）再定位一次副屏
        if (_mode == DisplayProjectionMode.Extend)
        {
            try
            {
                var (w, h, hz) = ResolveSecondaryMode();
                DisplayModeHelper.TrySetSecondaryMode(w, h, hz, _layoutSide);
                await Task.Delay(250).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        await RunHostCaptureAsync().ConfigureAwait(false);
    }

    private async Task RunHostCaptureAsync()
    {
        await StopCaptureAsync(releaseDisplay: false).ConfigureAwait(false);

        if (_mode == DisplayProjectionMode.Extend)
        {
            if (_vdd?.Target == null && DisplayMonitorEnum.FindBestExtendTarget() == null)
            {
                var (ok, err) = await EnsureHostDisplayAsync(layoutSide: _layoutSide).ConfigureAwait(false);
                if (!ok)
                {
                    await _service.StopAsync(err ?? "no-display").ConfigureAwait(false);
                    return;
                }
            }
        }

        var cts = new CancellationTokenSource();
        _captureCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _service.Role == ScreenExtendRole.Host)
            {
                var target = ResolveCaptureTarget();
                if (target == null)
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var frame = ScreenCaptureJpeg.Capture(target.Value);
                    if (frame is { Jpeg.Length: > 0 })
                    {
                        await _service.SendFrameAsync(frame.Value.Jpeg, frame.Value.Width, frame.Value.Height, token)
                            .ConfigureAwait(false);
                    }
                }
                catch { /* ignore frame errors */ }

                try { await Task.Delay(66, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private MonitorRect? ResolveCaptureTarget()
    {
        if (_mode == DisplayProjectionMode.Clone)
            return DisplayMonitorEnum.FindPrimary();

        // 扩展：每帧重枚举副屏；禁止回退主屏（否则从机看到本机桌面且分辨率错乱）
        var secondary = DisplayMonitorEnum.FindBestExtendTarget();
        if (secondary != null)
        {
            _vdd?.RefreshTarget(secondary.Value);
            return secondary;
        }

        return _vdd?.Target;
    }

    private void OnSinkReady()
    {
        _dispatcher.BeginInvoke(() =>
        {
            _sinkWindow ??= new ScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
            _sinkWindow.ShowFullscreen();
        });
    }

    private void OnFrame(ReadOnlyMemory<byte> jpeg, int w, int h)
    {
        var copy = jpeg.ToArray();
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _sinkWindow ??= new ScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
                if (!_sinkWindow.IsVisible)
                    _sinkWindow.ShowFullscreen();
                _sinkWindow.PresentJpeg(copy);
            }
            catch { /* ignore */ }
        });
    }

    private void OnSessionEnded()
    {
        // 对端 Esc/关窗/杀进程/断连：主机必须停采集、释放副屏并恢复主屏（勿看 !_wantDisplay）
        var releaseHost = _hostSessionActive || _wantDisplay || _vdd != null;
        _hostSessionActive = false;
        _wantDisplay = false;
        _ = FinalizeAfterSessionEndedAsync(releaseHost);
        _dispatcher.BeginInvoke(() =>
        {
            try { _sinkWindow?.CloseFromSession(); } catch { /* ignore */ }
            _sinkWindow = null;
        });
    }

    private readonly SemaphoreSlim _finalizeGate = new(1, 1);

    private async Task FinalizeAfterSessionEndedAsync(bool releaseHostDisplay)
    {
        await _finalizeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var usedVdd = _vdd?.UsedVirtualDriver == true;
            await StopCaptureAsync(releaseDisplay: releaseHostDisplay).ConfigureAwait(false);
            if (releaseHostDisplay)
                await DisplayTopologyGuard.FinalizeAfterExtendStopAsync(usedVdd).ConfigureAwait(false);
        }
        finally
        {
            _finalizeGate.Release();
        }
    }

    private async Task StopCaptureAsync(bool releaseDisplay)
    {
        try { _captureCts?.Cancel(); } catch { /* ignore */ }
        _captureCts?.Dispose();
        _captureCts = null;
        if (releaseDisplay && _vdd != null)
        {
            await _vdd.DisposeAsync().ConfigureAwait(false);
            _vdd = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var releaseHost = _hostSessionActive || _wantDisplay || _vdd != null;
        _hostSessionActive = false;
        _wantDisplay = false;
        _service.HostSessionReady -= OnHostReady;
        _service.SinkSessionReady -= OnSinkReady;
        _service.SessionEnded -= OnSessionEnded;
        _service.FrameReceived -= OnFrame;
        try
        {
            if (_service.IsActive)
                _service.StopAsync("app-exit").GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
        _ = FinalizeAfterSessionEndedAsync(releaseHost);
        _dispatcher.BeginInvoke(() =>
        {
            try { _sinkWindow?.CloseFromSession(); } catch { /* ignore */ }
            _sinkWindow = null;
        });
    }
}
