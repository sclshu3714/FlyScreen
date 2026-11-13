using System.Collections.Concurrent;
using System.Windows.Threading;
using FlyScreen.ScreenExtend;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;
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
    private readonly ProjectionAudioBridge _audio;
    private readonly ConcurrentDictionary<string, LatestFrameSlot> _framesByPeer =
        new(StringComparer.OrdinalIgnoreCase);
    private int _decodePosted;
    private long _hostReadyTick;

    public ScreenExtendController(ScreenExtendService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _audio = new ProjectionAudioBridge(service, new FlyScreen.ScreenExtend.WindowsProjectionAudio());
        _service.HostSessionReady += OnHostReady;
        _service.SinkSessionReady += OnSinkReady;
        _service.SessionEnded += OnSessionEnded;
        _service.FrameReceived += OnFrame;
        _service.ReverseInputReceived += OnReverseInput;
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

    public Func<IReadOnlyList<string>>? ListAddablePeers { get; set; }
    public Func<string, Task>? StartExtraProjection { get; set; }

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
        _hostReadyTick = Environment.TickCount64;
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

                var display = _display;
                var interval = ProjectionDisplayPresets.FpsToIntervalMs(display.Fps);
                var started = Environment.TickCount64;
                try
                {
                    var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var picture = ProjectionDisplayPresets.DescribeQuality(display.JpegQuality);
                    var maxEdge = picture.Passthrough ? 0 : Math.Max(display.Width, display.Height);
                    if (!picture.Passthrough && picture.ExtraLongEdgeCap > 0)
                        maxEdge = Math.Min(maxEdge, picture.ExtraLongEdgeCap);
                    var frame = ScreenCaptureJpeg.Capture(target.Value, picture.EncodeQuality, maxEdge);
                    if (frame is { Jpeg.Length: > 0 })
                    {
                        await _service.SendFrameAsync(frame.Value.Jpeg, frame.Value.Width, frame.Value.Height,
                                capturedAt, token)
                            .ConfigureAwait(false);
                    }
                }
                catch { /* ignore frame errors */ }

                var wait = interval - (int)(Environment.TickCount64 - started);
                if (wait <= 0) continue;
                try { await Task.Delay(wait, token).ConfigureAwait(false); }
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

    private void OnReverseInput(ScreenExtendInputPayload input)
    {
        try
        {
            var target = ResolveCaptureTarget();
            if (target == null) return;
            // 会话刚开始时丢掉从机全屏合成的左上角回控，避免光标闪到采集屏角落
            if (input.Kind == ScreenExtendInputPayload.KindMove
                && _hostReadyTick != 0
                && Environment.TickCount64 - _hostReadyTick < 500
                && input.Nx <= 0.03 && input.Ny <= 0.03)
                return;

            var rect = target.Value;
            var px = rect.Left + (int)Math.Round(Math.Clamp(input.Nx, 0, 1) * Math.Max(0, rect.Width - 1));
            var py = rect.Top + (int)Math.Round(Math.Clamp(input.Ny, 0, 1) * Math.Max(0, rect.Height - 1));

            if (input.Kind is ScreenExtendInputPayload.KindMove
                or ScreenExtendInputPayload.KindButton
                or ScreenExtendInputPayload.KindWheel)
                ProjectionHostCursor.BeginRemoteDriving();

            switch (input.Kind)
            {
                case ScreenExtendInputPayload.KindMove:
                    InputInjector.MoveAbsoluteDesktopPixels(px, py);
                    break;
                case ScreenExtendInputPayload.KindButton:
                    InputInjector.MoveAbsoluteDesktopPixels(px, py);
                    InputInjector.MouseButton(new MouseButtonPayload
                    {
                        Button = input.Button,
                        Down = input.Down
                    });
                    break;
                case ScreenExtendInputPayload.KindWheel:
                    InputInjector.MoveAbsoluteDesktopPixels(px, py);
                    InputInjector.MouseWheel(new MouseWheelPayload
                    {
                        Delta = input.Delta,
                        Horizontal = input.Horizontal
                    });
                    break;
                case ScreenExtendInputPayload.KindKey:
                    if (!string.IsNullOrEmpty(input.Text))
                        InputInjector.InsertText(input.Text);
                    else
                        InputInjector.Key(new KeyEventPayload
                        {
                            VirtualKey = input.VirtualKey,
                            Down = input.Down,
                            Extended = input.Extended
                        });
                    break;
            }
        }
        catch { /* ignore inject errors */ }
    }

    private void EnsureSinkWindow()
    {
        _sinkWindow ??= new ScreenExtendSinkWindow(
            peer => _ = _service.StopSessionAsync(peer, "sink-close"),
            () => _ = _service.StopAsync("sink-close"),
            () => ListAddablePeers?.Invoke() ?? [],
            remote => StartExtraProjection?.Invoke(remote) ?? Task.CompletedTask);
    }

    private void OnSinkReady(string peer)
    {
        _dispatcher.BeginInvoke(() =>
        {
            EnsureSinkWindow();
            _sinkWindow!.AddOrActivateTab(peer, _service);
            _sinkWindow.ShowFullscreen();
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
                        if (src == null || !slot.IsCurrent(gen)) continue;
                        var shown = src.Value;
                        var g = gen;
                        var capturedUnixMs = capturedAt;
                        var peer = peerKey;
                        var frameSlot = slot;
                        _dispatcher.BeginInvoke(() =>
                        {
                            if (_disposed || _service.Role != ScreenExtendRole.Sink || !frameSlot.IsCurrent(g))
                                return;
                            try
                            {
                                EnsureSinkWindow();
                                // 收帧只 Ensure，禁止强制切 Tab（否则多路会互相抢激活导致闪烁/无法切换）
                                _sinkWindow!.EnsureTab(peer, _service);
                                if (!_sinkWindow.IsVisible)
                                    _sinkWindow.ShowFullscreen();
                                _sinkWindow.Present(peer, shown, capturedUnixMs);
                            }
                            catch { /* ignore */ }
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
        _dispatcher.BeginInvoke(ProjectionHostCursor.EndSessionOnHookThread);

        if (!string.IsNullOrWhiteSpace(peer)
            && _framesByPeer.TryRemove(peer.Trim(), out var removed))
            removed.Clear();

        var wasHost = _hostSessionActive;
        var hostEnded = wasHost && _service.Role != ScreenExtendRole.Host;
        if (hostEnded)
        {
            var releaseHost = wasHost || _wantDisplay || _vdd != null;
            _hostSessionActive = false;
            _wantDisplay = false;
            foreach (var s in _framesByPeer.Values) s.Clear();
            _framesByPeer.Clear();
            _ = FinalizeAfterSessionEndedAsync(releaseHost);
        }

        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(peer))
                    _sinkWindow?.RemoveTab(peer);

                if (_sinkWindow != null && !_sinkWindow.HasTabs)
                {
                    _sinkWindow.CloseFromSession();
                    _sinkWindow = null;
                }
            }
            catch { /* ignore */ }
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
        _audio.Dispose();
        var releaseHost = _hostSessionActive || _wantDisplay || _vdd != null;
        _hostSessionActive = false;
        _wantDisplay = false;
        _service.HostSessionReady -= OnHostReady;
        _service.SinkSessionReady -= OnSinkReady;
        _service.SessionEnded -= OnSessionEnded;
        _service.FrameReceived -= OnFrame;
        _service.ReverseInputReceived -= OnReverseInput;
        try
        {
            if (_service.IsActive)
                _service.StopAsync("app-exit").GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        // 必须等 Finalize（/internal + 清脏标记）完成再让进程退出，否则托盘退出会残留扩展拓扑
        try
        {
            FinalizeAfterSessionEndedAsync(releaseHost).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        _dispatcher.BeginInvoke(() =>
        {
            try { _sinkWindow?.CloseFromSession(); } catch { /* ignore */ }
            _sinkWindow = null;
        });
    }
}
