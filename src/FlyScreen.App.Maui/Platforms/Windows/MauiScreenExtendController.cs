using System.Collections.Concurrent;
using FlyScreen.Native;
using FlyScreen.ScreenExtend;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>Maui-Win：主机推流 + 从机全屏（WinUI Window）。</summary>
public sealed class MauiScreenExtendController : IDisposable
{
    private readonly ScreenExtendService _service;
    private VirtualDisplaySession? _vdd;
    private CancellationTokenSource? _captureCts;
    private MauiScreenExtendSinkWindow? _sink;
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

    public Func<IReadOnlyList<string>>? ListAddablePeers { get; set; }
    public Func<string, Task>? StartExtraProjection { get; set; }

    public MauiScreenExtendController(ScreenExtendService service)
    {
        _service = service;
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

    public void SetProjectionMode(DisplayProjectionMode mode) => _mode = mode;

    /// <summary>设备管理的远程投影固定复制主屏，不占用虚拟副屏。</summary>
    private bool UsesExtendDisplay
        => _mode == DisplayProjectionMode.Extend
           && _display.Source != ProjectionDisplaySource.HostPrimary;

    public void SetDisplayChoice(ProjectionDisplayChoice? choice)
        => _display = choice?.Clone() ?? new ProjectionDisplayChoice();

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

    private readonly SemaphoreSlim _finalizeGate = new(1, 1);

    public async Task ReleaseHostDisplayAsync()
    {
        _wantDisplay = false;
        await FinalizeAfterSessionEndedAsync(releaseHostDisplay: true).ConfigureAwait(false);
    }

    private long _hostReadyTick;

    private void OnHostReady()
    {
        _hostSessionActive = true;
        _hostReadyTick = Environment.TickCount64;
        MainThread.BeginInvokeOnMainThread(() => MauiProjectionTitleBar.SetProjecting(true));
        _ = OnHostReadyAsync();
    }

    private async Task OnHostReadyAsync()
    {
        if (UsesExtendDisplay)
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

        if (UsesExtendDisplay)
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
                catch { /* ignore */ }

                var wait = interval - (int)(Environment.TickCount64 - started);
                if (wait <= 0) continue;
                try { await Task.Delay(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    private MonitorRect? ResolveCaptureTarget()
    {
        if (!UsesExtendDisplay)
            return DisplayMonitorEnum.FindPrimary();

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
                    MauiInputInjector.MoveAbsoluteDesktopPixels(px, py);
                    break;
                case ScreenExtendInputPayload.KindButton:
                    MauiInputInjector.MoveAbsoluteDesktopPixels(px, py);
                    MauiInputInjector.MouseButton(new MouseButtonPayload
                    {
                        Button = input.Button,
                        Down = input.Down
                    });
                    break;
                case ScreenExtendInputPayload.KindWheel:
                    MauiInputInjector.MoveAbsoluteDesktopPixels(px, py);
                    MauiInputInjector.MouseWheel(new MouseWheelPayload
                    {
                        Delta = input.Delta,
                        Horizontal = input.Horizontal
                    });
                    break;
                case ScreenExtendInputPayload.KindKey:
                    if (!string.IsNullOrEmpty(input.Text))
                        MauiInputInjector.InsertText(input.Text);
                    else
                        MauiInputInjector.Key(new KeyEventPayload
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
        _sink ??= new MauiScreenExtendSinkWindow(
            peer => _ = _service.StopSessionAsync(peer, "sink-close"),
            () => _ = _service.StopAsync("sink-close"),
            () => ListAddablePeers?.Invoke() ?? [],
            remote => StartExtraProjection?.Invoke(remote) ?? Task.CompletedTask);
    }

    private void OnSinkReady(string peer)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            EnsureSinkWindow();
            _sink!.AddOrActivateTab(peer, _service);
            _sink.ShowFullscreen();
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
        _ = DecodeLoopAsync();
    }

    private bool AnyFramePending()
    {
        foreach (var slot in _framesByPeer.Values)
        {
            if (slot.HasPending) return true;
        }

        return false;
    }

    private async Task DecodeLoopAsync()
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
                        DecodedBgra? frame = null;
                        try
                        {
                            frame = await DecodeBgraAsync(jpeg).ConfigureAwait(false);
                        }
                        catch
                        {
                            continue;
                        }

                        if (frame == null) continue;
                        if (!slot.IsCurrent(gen) || _service.Role != ScreenExtendRole.Sink)
                            continue;

                        var shown = frame.Value;
                        var g = gen;
                        var capturedUnixMs = capturedAt;
                        var peer = peerKey;
                        var frameSlot = slot;
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (_disposed || _service.Role != ScreenExtendRole.Sink || !frameSlot.IsCurrent(g))
                                return;

                            try
                            {
                                EnsureSinkWindow();
                                _sink!.EnsureTab(peer, _service);
                                if (!_sink.IsVisible)
                                    _sink.ShowFullscreen();
                                _sink.Present(peer, shown.Width, shown.Height, shown.Pixels, capturedUnixMs);
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

    private readonly record struct DecodedBgra(int Width, int Height, byte[] Pixels);

    private static async Task<DecodedBgra?> DecodeBgraAsync(byte[] jpeg)
    {
        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(jpeg.AsBuffer()).AsTask().ConfigureAwait(false);
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras).AsTask().ConfigureAwait(false);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
        var pixels = data.DetachPixelData();
        var w = (int)decoder.PixelWidth;
        var h = (int)decoder.PixelHeight;
        if (w <= 0 || h <= 0 || pixels.Length < w * 4 * h)
            return null;
        return new DecodedBgra(w, h, pixels);
    }

    private void OnSessionEnded(string? peer)
    {
        MainThread.BeginInvokeOnMainThread(ProjectionHostCursor.EndSessionOnHookThread);

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

        MainThread.BeginInvokeOnMainThread(() =>
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

            if (!_hostSessionActive && _sink == null)
                MauiProjectionTitleBar.SetProjecting(false);
        });
    }

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

        // 必须等 Finalize（/internal + 清脏标记）完成再让进程退出
        try
        {
            FinalizeAfterSessionEndedAsync(releaseHost).GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { _sink?.CloseFromSession(); } catch { /* ignore */ }
            _sink = null;
            MauiProjectionTitleBar.SetProjecting(false);
        });
    }
}

/// <summary>WinUI 投影播放器：自定义标题栏 + 多 Tab 信号延迟。</summary>
public sealed class MauiScreenExtendSinkWindow
{
    private readonly Action<string> _onStopTab;
    private readonly Action _onCloseWindow;
    private readonly Func<IReadOnlyList<string>> _listAddablePeers;
    private readonly Func<string, Task> _startProjection;
    private readonly SinkLocalCursorIdleHide _cursorIdle = new();
    private readonly ScreenExtendReverseMoveGate _reverseGate = new();
    private readonly List<SinkTabState> _tabs = [];
    private string? _activePeer;
    private readonly Microsoft.UI.Xaml.Window _window;
    private readonly Microsoft.UI.Xaml.Controls.Grid _shell;
    private readonly Microsoft.UI.Xaml.Controls.Border _titleBar;
    private readonly Microsoft.UI.Xaml.Controls.StackPanel _tabsHost;
    private readonly Microsoft.UI.Xaml.Controls.Button _addTabBtn;
    private readonly Microsoft.UI.Xaml.Controls.Grid _contentHost;
    private readonly Microsoft.UI.Xaml.Controls.Image _image;
    private readonly Microsoft.UI.Xaml.Controls.TextBlock _waiting;
    private readonly Microsoft.UI.Xaml.Controls.Button _fullscreenBtn;
    private readonly Microsoft.UI.Xaml.Controls.Canvas _fsToolbarLayer;
    private readonly Microsoft.UI.Xaml.Controls.Border _fsToolbar;
    private readonly Microsoft.UI.Xaml.Controls.StackPanel _fsToolbarExtra;
    private readonly Microsoft.UI.Xaml.Controls.Button _fsGripBtn;
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private int _showCursorHideDepth;
    private ScreenExtendService? _session;
    private bool _captureBound;
    private bool _windowActive;
    private bool _chromeFullscreen;
    private bool _fsToolbarExpanded = true;
    private bool _fsToolbarDragging;
    private bool _fsToolbarMoved;
    private double _fsDragStartX;
    private double _fsDragStartY;
    private double _fsToolbarOriginX;
    private double _fsToolbarOriginY;
    private string _latencyTip = "";

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TabActiveBg = HexBrushStatic("#2C2C30");
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TabInactiveBg = HexBrushStatic("#18181B");

    private sealed class SinkTabState
    {
        public required string Peer { get; init; }
        public required Microsoft.UI.Xaml.Controls.Border TabBorder { get; init; }
        public required Microsoft.UI.Xaml.Controls.StackPanel SignalPanel { get; init; }
        public required Microsoft.UI.Xaml.Controls.TextBlock NameText { get; init; }
        public Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? FrameBitmap;
        public int ContentW;
        public int ContentH;
        public double LatencyEma;
        public long LatencySkewBaseline = long.MinValue;
        public ProjectionLatency.Signal LastSignal;
        public string LatencyTip = "";
    }

    public MauiScreenExtendSinkWindow(
        Action<string> onStopTab,
        Action onCloseWindow,
        Func<IReadOnlyList<string>> listAddablePeers,
        Func<string, Task> startProjection)
    {
        _onStopTab = onStopTab;
        _onCloseWindow = onCloseWindow;
        _listAddablePeers = listAddablePeers;
        _startProjection = startProjection;
        _image = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.None,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        _waiting = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "等待投影画面…",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            Opacity = 0.7,
            FontSize = 18,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };

        _tabsHost = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal
        };
        _addTabBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "+",
                FontSize = 16,
                Foreground = HexBrush("#9CA3AF"),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            },
            Width = 28,
            Height = 28,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 4, 4),
            Padding = new Microsoft.UI.Xaml.Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_addTabBtn, "添加投影");
        _addTabBtn.Click += (_, _) => ShowAddTabMenu();

        var titleTabRow = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom,
            Margin = new Microsoft.UI.Xaml.Thickness(8, 4, 0, 0),
            Children =
            {
                _tabsHost,
                new Microsoft.UI.Xaml.Controls.Border
                {
                    Width = 1,
                    Height = 18,
                    Background = HexBrush("#3F3F46"),
                    Margin = new Microsoft.UI.Xaml.Thickness(6, 0, 4, 4),
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom
                },
                _addTabBtn
            }
        };

        _fullscreenBtn = ChromeGlyphBtn("⛶", 46, ToggleFullscreen);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fullscreenBtn, "全屏");
        var chrome = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Children =
            {
                _fullscreenBtn,
                ChromeGlyphBtn("─", 46, () => SetWindowState(minimized: true)),
                ChromeGlyphBtn("□", 46, ToggleMaximize),
                ChromeGlyphBtn("✕", 46, CloseWindow)
            }
        };

        var titleGrid = new Microsoft.UI.Xaml.Controls.Grid();
        titleGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        titleGrid.Children.Add(titleTabRow);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(chrome, 2);
        titleGrid.Children.Add(chrome);

        _titleBar = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = HexBrush("#1A1A1D"),
            Height = 36,
            Child = titleGrid
        };

        _contentHost = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
        };
        _contentHost.Children.Add(new Microsoft.UI.Xaml.Controls.Viewbox
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            StretchDirection = Microsoft.UI.Xaml.Controls.StretchDirection.Both,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
            Child = _image
        });
        _contentHost.Children.Add(_waiting);
        _contentHost.PointerMoved += (_, _) => _cursorIdle.NotifyLocalMouseMoved();

        _latencyTip = FlyScreen.Helpers.Localization.Loc.T("Proj.LatencyTipWait");
        _fsGripBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "◂▸",
            Width = 28,
            Height = 28,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            Foreground = HexBrush("#C8C8CE"),
            FontSize = 11,
            Padding = new Microsoft.UI.Xaml.Thickness(0)
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fsGripBtn, _latencyTip);
        _fsGripBtn.PointerPressed += FsGrip_PointerPressed;
        _fsGripBtn.PointerMoved += FsGrip_PointerMoved;
        _fsGripBtn.PointerReleased += FsGrip_PointerReleased;
        _fsGripBtn.PointerCaptureLost += (_, _) =>
        {
            _fsToolbarDragging = false;
            _fsToolbarMoved = false;
        };

        _fsToolbarExtra = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Children =
            {
                FsToolBtn("⇄", SelectNextTab),
                FsToolBtn("⛶", ToggleFullscreen),
                FsToolBtn("⊗", CloseCurrentTab)
            }
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fsToolbarExtra.Children[0], "切换标签");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fsToolbarExtra.Children[1], "退出全屏");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fsToolbarExtra.Children[2], "结束投影");

        _fsToolbar = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = HexBrush("#E01A1A1D"),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
            BorderBrush = HexBrush("#33FFFFFF"),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            Padding = new Microsoft.UI.Xaml.Thickness(2),
            Child = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Children = { _fsGripBtn, _fsToolbarExtra }
            }
        };
        _fsToolbar.PointerEntered += (_, _) =>
        {
            _cursorIdle.NotifyLocalMouseMoved();
            ForceShowLocalCursor();
        };
        _fsToolbar.PointerExited += (_, _) =>
        {
            if (_chromeFullscreen && !_fsToolbarDragging)
                ForceHideLocalCursor();
        };

        _fsToolbarLayer = new Microsoft.UI.Xaml.Controls.Canvas
        {
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch
        };
        _fsToolbarLayer.Children.Add(_fsToolbar);
        _fsToolbarLayer.PointerMoved += FsGrip_PointerMoved;
        _fsToolbarLayer.PointerReleased += FsGrip_PointerReleased;
        _fsToolbarLayer.SizeChanged += (_, _) =>
        {
            if (_chromeFullscreen)
                ResetFsToolbarToTopCenter();
        };

        _shell = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = HexBrush("#0B0B0C")
        };
        _shell.RowDefinitions.Add(new Microsoft.UI.Xaml.Controls.RowDefinition { Height = new Microsoft.UI.Xaml.GridLength(36) });
        _shell.RowDefinitions.Add(new Microsoft.UI.Xaml.Controls.RowDefinition { Height = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        _shell.Children.Add(_titleBar);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(_contentHost, 1);
        _shell.Children.Add(_contentHost);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_fsToolbarLayer, 2);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_fsToolbarLayer, 100);
        _shell.Children.Add(_fsToolbarLayer);
        _shell.IsTabStop = true;
        _shell.KeyDown += Root_KeyDown;
        _shell.KeyUp += Root_KeyUp;
        _cursorIdle.ShowLocalCursorChanged += ApplyLocalCursor;

        _window = new Microsoft.UI.Xaml.Window { Title = "玄鸟投影", Content = _shell };
        _window.Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState
                != Microsoft.UI.Xaml.WindowActivationState.Deactivated;
            if (_windowActive)
            {
                try { _shell.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { /* ignore */ }
                TryAttachCapture();
            }
            else
            {
                DetachCapture();
            }
        };
        _window.Closed += (_, _) =>
        {
            DetachCapture();
            ForceShowLocalCursor();
            _cursorIdle.EndSession();
            _cursorIdle.Dispose();
            NotifyWindowClose();
        };
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.Closing += (_, args) =>
            {
                if (_closingFromSession) return;
                NotifyWindowClose();
            };
        }
        catch { /* Activate 后再绑 Closing 亦可 */ }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush HexBrush(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        var v = Convert.ToUInt32(hex, 16);
        return new(global::Windows.UI.Color.FromArgb(
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v));
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush HexBrushStatic(string hex) => HexBrush(hex);

    private static Microsoft.UI.Xaml.Controls.Button ChromeGlyphBtn(string glyph, double width, Action click)
    {
        var btn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = glyph,
            Width = width,
            Height = 32,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            Foreground = HexBrush("#D1D5DB"),
            FontSize = 12,
            Padding = new Microsoft.UI.Xaml.Thickness(0)
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    private static Microsoft.UI.Xaml.Controls.Button FsToolBtn(string glyph, Action click)
    {
        var btn = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = glyph,
            Width = 28,
            Height = 28,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
            Foreground = HexBrush("#C8C8CE"),
            FontSize = 12,
            Padding = new Microsoft.UI.Xaml.Thickness(0)
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    public bool HasTabs => _tabs.Count > 0;

    public void EnsureTab(string peer, ScreenExtendService? service = null)
    {
        if (service != null)
            _session = service;
        peer = peer.Trim();
        if (string.IsNullOrEmpty(peer)) return;
        if (FindTab(peer) != null) return;

        var created = CreateTabUi(peer);
        _tabs.Add(created);
        _tabsHost.Children.Add(created.TabBorder);
        if (string.IsNullOrEmpty(_activePeer))
            SetActiveTab(peer);
        else
            RefreshTabChrome();
    }

    public void AddOrActivateTab(string peer, ScreenExtendService? service = null)
    {
        EnsureTab(peer, service);
        if (HasTab(peer))
            SetActiveTab(peer);
    }

    public void RemoveTab(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return;
        peer = peer.Trim();
        var idx = _tabs.FindIndex(t => string.Equals(t.Peer, peer, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        var removed = _tabs[idx];
        _tabsHost.Children.Remove(removed.TabBorder);
        _tabs.RemoveAt(idx);

        if (_tabs.Count == 0)
        {
            _activePeer = null;
            _image.Source = null;
            _waiting.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            return;
        }

        if (string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase))
            SetActiveTab(_tabs[Math.Min(idx, _tabs.Count - 1)].Peer);
        else
            RefreshTabChrome();
    }

    private SinkTabState CreateTabUi(string peer)
    {
        var signalPanel = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 1.3,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Width = 14,
            Height = 12
        };
        var nameText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = peer,
            Foreground = HexBrush("#F3F4F6"),
            FontSize = 12,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            MaxWidth = 180,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
        };
        var closeBtn = ChromeGlyphBtn("×", 18, () => RequestStopTab(peer));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(closeBtn, "结束投影");

        var tabBody = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Children = { signalPanel, nameText, closeBtn }
        };

        var tabBorder = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = TabInactiveBg,
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6, 6, 0, 0),
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 4, 0),
            Padding = new Microsoft.UI.Xaml.Thickness(10, 0, 6, 0),
            MinWidth = 120,
            Height = 28,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom,
            Child = tabBody,
            Tag = peer
        };
        tabBorder.PointerPressed += (_, e) =>
        {
            if (tabBorder.Tag is string p && e.GetCurrentPoint(tabBorder).Properties.IsLeftButtonPressed)
            {
                SetActiveTab(p);
                e.Handled = true;
            }
        };

        var state = new SinkTabState
        {
            Peer = peer,
            TabBorder = tabBorder,
            SignalPanel = signalPanel,
            NameText = nameText,
            LatencyTip = FlyScreen.Helpers.Localization.Loc.T("Proj.LatencyTipWait")
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tabBorder, state.LatencyTip);
        DrawSignalOn(signalPanel, ref state.LastSignal, new ProjectionLatency.Signal(0, 0));
        return state;
    }

    private void SetActiveTab(string peer)
    {
        peer = peer.Trim();
        if (!HasTab(peer)) return;
        var same = string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase);
        _activePeer = peer;
        _session?.SetActiveSinkPeer(peer);
        RefreshTabChrome();
        RefreshTitle();
        ShowActiveTabFrame();
        ApplyActiveLatencyTip();
        if (same) return;
        DetachCapture();
        TryAttachCapture();
    }

    private void SelectNextTab()
    {
        if (_tabs.Count <= 1) return;
        var idx = _tabs.FindIndex(t => string.Equals(t.Peer, _activePeer, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = 0;
        SetActiveTab(_tabs[(idx + 1) % _tabs.Count].Peer);
    }

    private void RefreshTabChrome()
    {
        foreach (var t in _tabs)
        {
            var active = string.Equals(t.Peer, _activePeer, StringComparison.OrdinalIgnoreCase);
            t.TabBorder.Background = active ? TabActiveBg : TabInactiveBg;
        }
    }

    private void RefreshTitle()
    {
        var peer = _activePeer?.Trim();
        _window.Title = string.IsNullOrWhiteSpace(peer) ? "玄鸟投影" : $"玄鸟投影 · {peer}";
    }

    private void ShowActiveTabFrame()
    {
        var tab = FindTab(_activePeer);
        if (tab?.FrameBitmap != null)
        {
            _image.Source = tab.FrameBitmap;
            _waiting.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        else
        {
            _image.Source = null;
            _waiting.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
    }

    private SinkTabState? FindTab(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return null;
        return _tabs.Find(t => string.Equals(t.Peer, peer.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool HasTab(string peer) => FindTab(peer) != null;

    private int ActiveContentW => FindTab(_activePeer)?.ContentW ?? 0;
    private int ActiveContentH => FindTab(_activePeer)?.ContentH ?? 0;

    private void RequestStopTab(string peer)
    {
        try { _onStopTab(peer.Trim()); } catch { /* ignore */ }
    }

    private void CloseCurrentTab()
    {
        if (!string.IsNullOrWhiteSpace(_activePeer))
            RequestStopTab(_activePeer);
    }

    private void CloseWindow()
    {
        NotifyWindowClose();
        try { _window.Close(); } catch { /* ignore */ }
    }

    private void ShowAddTabMenu()
    {
        IReadOnlyList<string> items;
        try { items = _listAddablePeers(); }
        catch { items = []; }

        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        if (items.Count == 0)
        {
            var disabled = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
            {
                Text = "无可用设备",
                IsEnabled = false,
                Icon = CreateAddMenuDeviceIcon(muted: true)
            };
            flyout.Items.Add(disabled);
        }
        else
        {
            foreach (var name in items)
            {
                var remote = name;
                var mi = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
                {
                    Text = remote,
                    Tag = remote,
                    Icon = CreateAddMenuDeviceIcon(muted: false)
                };
                mi.Click += async (_, _) =>
                {
                    if (mi.Tag is string r)
                    {
                        try { await _startProjection(r); }
                        catch { /* ignore */ }
                    }
                };
                flyout.Items.Add(mi);
            }
        }

        flyout.ShowAt(_addTabBtn);
    }

    private static Microsoft.UI.Xaml.Controls.FontIcon CreateAddMenuDeviceIcon(bool muted)
        => new()
        {
            // Segoe Fluent: 电脑显示器
            Glyph = "\uE7F8",
            FontSize = 14,
            Foreground = HexBrush(muted ? "#6B7280" : "#D1D5DB")
        };

    private void ToggleFullscreen()
    {
        _chromeFullscreen = !_chromeFullscreen;
        ApplyChromeFullscreen();
    }

    private void ToggleMaximize()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                if (op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                    op.Restore();
                else
                    op.Maximize();
            }
        }
        catch { /* ignore */ }
    }

    private void SetWindowState(bool minimized)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op && minimized)
                op.Minimize();
        }
        catch { /* ignore */ }
    }

    private void ApplyChromeFullscreen()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            if (_chromeFullscreen)
            {
                // 全屏：盖住任务栏 + 浮动工具条
                _titleBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                _shell.RowDefinitions[0].Height = new Microsoft.UI.Xaml.GridLength(0);
                _fsToolbarExpanded = true;
                _fsToolbarExtra.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                _fsToolbarLayer.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                    id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                if (area != null)
                    appWindow.MoveAndResize(area.OuterBounds);
                appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                _cursorIdle.BeginSession();
                ForceHideLocalCursor();
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fullscreenBtn, "退出全屏");
                _ = _fsToolbarLayer.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                    ResetFsToolbarToTopCenter);
            }
            else
            {
                // 最大化：工作区，不盖任务栏
                _titleBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                _shell.RowDefinitions[0].Height = new Microsoft.UI.Xaml.GridLength(36);
                _fsToolbarLayer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                _fsToolbarDragging = false;
                appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                {
                    op.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
                    op.Maximize();
                }
                _cursorIdle.EndSession();
                ForceShowLocalCursor();
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fullscreenBtn, "全屏");
            }
        }
        catch { /* ignore */ }
    }

    public void BindSession(ScreenExtendService service)
    {
        _session = service;
        var peer = service.Peer?.Trim();
        if (!string.IsNullOrWhiteSpace(peer))
            AddOrActivateTab(peer, service);
    }

    private void TryAttachCapture()
    {
        if (_captureBound) return;
        if (_session?.CanSendReverseInput != true) return;
        _captureBound = true;
        _reverseGate.OnCaptureAttached();
        _contentHost.PointerMoved += OnPointerMoved;
        _contentHost.PointerPressed += OnPointerPressed;
        _contentHost.PointerReleased += OnPointerReleased;
        _contentHost.PointerWheelChanged += OnPointerWheel;
    }

    private void DetachCapture()
    {
        if (!_captureBound) return;
        _captureBound = false;
        _reverseGate.OnCaptureDetached();
        _contentHost.PointerMoved -= OnPointerMoved;
        _contentHost.PointerPressed -= OnPointerPressed;
        _contentHost.PointerReleased -= OnPointerReleased;
        _contentHost.PointerWheelChanged -= OnPointerWheel;
    }

    private bool TryMap(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, out double nx, out double ny)
    {
        var pt = e.GetCurrentPoint(_contentHost).Position;
        return ScreenExtendReverseInput.TryMapUniformContent(
            _contentHost.ActualWidth, _contentHost.ActualHeight, ActiveContentW, ActiveContentH, pt.X, pt.Y, out nx, out ny);
    }

    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendMove(nx, ny)) return;
        _ = _session.SendReverseMoveAsync(nx, ny);
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: true);
    }

    private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: false);
    }

    private void OnPointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var props = e.GetCurrentPoint(_contentHost).Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        _ = _session.SendReverseWheelAsync(nx, ny, delta, horizontal: false);
    }

    private static int MapButton(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed
            || props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
            return 0;
        if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed
            || props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased)
            return 1;
        if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed
            || props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased)
            return 2;
        if (props.IsLeftButtonPressed) return 0;
        if (props.IsRightButtonPressed) return 1;
        if (props.IsMiddleButtonPressed) return 2;
        return -1;
    }

    private void ApplyLocalCursor(bool show)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (show) ForceShowLocalCursor();
                else ForceHideLocalCursor();
            }
            catch { /* ignore */ }
        });
    }

    private void ForceHideLocalCursor()
    {
        while (NativeMethods.ShowCursor(false) >= 0)
            _showCursorHideDepth++;
        if (_showCursorHideDepth == 0)
            _showCursorHideDepth = 1;
    }

    private void ForceShowLocalCursor()
    {
        while (_showCursorHideDepth > 0)
        {
            NativeMethods.ShowCursor(true);
            _showCursorHideDepth--;
        }
        while (NativeMethods.ShowCursor(true) < 0) { /* restore display count */ }
    }

    private void NotifyWindowClose()
    {
        if (_closingFromSession || _userCloseRaised) return;
        _userCloseRaised = true;
        try { _onCloseWindow(); } catch { /* ignore */ }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Tab && _chromeFullscreen)
        {
            SelectNextTab();
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_chromeFullscreen)
            {
                _chromeFullscreen = false;
                ApplyChromeFullscreen();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_activePeer))
                RequestStopTab(_activePeer);
            return;
        }

        if (_session?.CanSendReverseInput != true) return;
        var vk = (ushort)e.Key;
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: true, extended: IsExtended(e.Key));
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape) return;
        if (_session?.CanSendReverseInput != true) return;
        var vk = (ushort)e.Key;
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: false, extended: IsExtended(e.Key));
    }

    private static bool IsExtended(global::Windows.System.VirtualKey key)
        => key is global::Windows.System.VirtualKey.Left or global::Windows.System.VirtualKey.Right
            or global::Windows.System.VirtualKey.Up or global::Windows.System.VirtualKey.Down
            or global::Windows.System.VirtualKey.Insert or global::Windows.System.VirtualKey.Delete
            or global::Windows.System.VirtualKey.Home or global::Windows.System.VirtualKey.End
            or global::Windows.System.VirtualKey.PageUp or global::Windows.System.VirtualKey.PageDown
            or global::Windows.System.VirtualKey.NumberKeyLock
            or global::Windows.System.VirtualKey.LeftControl or global::Windows.System.VirtualKey.RightControl
            or global::Windows.System.VirtualKey.LeftMenu or global::Windows.System.VirtualKey.RightMenu;

    public bool IsVisible
    {
        get
        {
            try { return _window.Visible; }
            catch { return false; }
        }
    }

    public void ShowFullscreen()
    {
        // 默认：最大化到工作区（不盖任务栏）+ 标题栏
        _chromeFullscreen = false;
        _cursorIdle.EndSession();
        ForceShowLocalCursor();
        _titleBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        _fsToolbarLayer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        _shell.RowDefinitions[0].Height = new Microsoft.UI.Xaml.GridLength(36);
        _window.Activate();
        try { _shell.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { /* ignore */ }
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
                op.Maximize();
            }
            appWindow.Show();
        }
        catch
        {
            _window.Activate();
        }

        _windowActive = true;
        TryAttachCapture();
    }

    public void Present(string peer, int width, int height, byte[] pixels, long capturedUnixMs = 0)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * 4 * height)
            return;

        peer = peer.Trim();
        EnsureTab(peer);
        var tab = FindTab(peer);
        if (tab == null) return;

        tab.ContentW = width;
        tab.ContentH = height;
        if (tab.FrameBitmap == null || tab.FrameBitmap.PixelWidth != width || tab.FrameBitmap.PixelHeight != height)
            tab.FrameBitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);

        using (var stream = tab.FrameBitmap.PixelBuffer.AsStream())
        {
            stream.Position = 0;
            stream.Write(pixels, 0, width * 4 * height);
        }

        tab.FrameBitmap.Invalidate();
        UpdateLatencyForTab(tab, capturedUnixMs);

        if (!string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase))
            return;

        _image.Source = tab.FrameBitmap;
        if (_waiting.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
            _waiting.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void UpdateLatencyForTab(SinkTabState tab, long capturedUnixMs)
    {
        var sample = ProjectionLatency.ComputeMs(capturedUnixMs, ref tab.LatencySkewBaseline);
        if (sample == null)
        {
            if (tab.LatencyEma <= 0)
            {
                ApplyLatencyTipForTab(tab, FlyScreen.Helpers.Localization.Loc.T("Proj.LatencyTipWait"));
                DrawSignalOn(tab.SignalPanel, ref tab.LastSignal, new ProjectionLatency.Signal(0, 0));
            }
            return;
        }

        var ms = ProjectionLatency.Smooth(sample, ref tab.LatencyEma);
        ApplyLatencyTipForTab(tab, string.Format(FlyScreen.Helpers.Localization.Loc.T("Proj.LatencyTip"), ms));
        DrawSignalOn(tab.SignalPanel, ref tab.LastSignal, ProjectionLatency.ToSignal(ms));
    }

    private void ApplyLatencyTipForTab(SinkTabState tab, string tip)
    {
        tab.LatencyTip = tip;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(tab.TabBorder, tip);
        if (string.Equals(tab.Peer, _activePeer, StringComparison.OrdinalIgnoreCase))
            ApplyActiveLatencyTip();
    }

    private void ApplyActiveLatencyTip()
    {
        var tab = FindTab(_activePeer);
        _latencyTip = tab?.LatencyTip ?? FlyScreen.Helpers.Localization.Loc.T("Proj.LatencyTipWait");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_fsGripBtn, _latencyTip);
    }

    private void ResetFsToolbarToTopCenter()
    {
        if (!_chromeFullscreen) return;
        _fsToolbar.UpdateLayout();
        var tw = _fsToolbar.ActualWidth > 0 ? _fsToolbar.ActualWidth : 90;
        var layerW = _fsToolbarLayer.ActualWidth;
        if (layerW <= 0) return;
        var x = Math.Max(8, (layerW - tw) / 2);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_fsToolbar, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_fsToolbar, 10);
    }

    private void ToggleFsToolbarExpanded()
    {
        _fsToolbarExpanded = !_fsToolbarExpanded;
        _fsToolbarExtra.Visibility = _fsToolbarExpanded
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        PlaceFsToolbar(
            Microsoft.UI.Xaml.Controls.Canvas.GetLeft(_fsToolbar),
            Microsoft.UI.Xaml.Controls.Canvas.GetTop(_fsToolbar));
    }

    private void PlaceFsToolbar(double x, double y)
    {
        _fsToolbar.UpdateLayout();
        var tw = Math.Max(28, _fsToolbar.ActualWidth);
        var th = Math.Max(28, _fsToolbar.ActualHeight);
        var maxX = Math.Max(0, _fsToolbarLayer.ActualWidth - tw);
        var maxY = Math.Max(0, _fsToolbarLayer.ActualHeight - th);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_fsToolbar, Math.Clamp(x, 0, maxX));
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_fsToolbar, Math.Clamp(y, 0, maxY));
    }

    private void FsGrip_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_fsToolbarLayer);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _fsToolbarDragging = true;
        _fsToolbarMoved = false;
        _fsDragStartX = pt.Position.X;
        _fsDragStartY = pt.Position.Y;
        var left = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(_fsToolbar);
        var top = Microsoft.UI.Xaml.Controls.Canvas.GetTop(_fsToolbar);
        _fsToolbarOriginX = double.IsNaN(left) ? 0 : left;
        _fsToolbarOriginY = double.IsNaN(top) ? 0 : top;
        _fsGripBtn.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void FsGrip_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_fsToolbarDragging) return;
        var pt = e.GetCurrentPoint(_fsToolbarLayer);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var dx = pt.Position.X - _fsDragStartX;
        var dy = pt.Position.Y - _fsDragStartY;
        if (!_fsToolbarMoved && dx * dx + dy * dy < 16) return;
        _fsToolbarMoved = true;
        PlaceFsToolbar(_fsToolbarOriginX + dx, _fsToolbarOriginY + dy);
        _cursorIdle.NotifyLocalMouseMoved();
        e.Handled = true;
    }

    private void FsGrip_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_fsToolbarDragging) return;
        try { _fsGripBtn.ReleasePointerCapture(e.Pointer); } catch { /* ignore */ }
        var wasDrag = _fsToolbarMoved;
        _fsToolbarDragging = false;
        _fsToolbarMoved = false;
        if (!wasDrag)
            ToggleFsToolbarExpanded();
        e.Handled = true;
    }

    private static void DrawSignalOn(
        Microsoft.UI.Xaml.Controls.StackPanel panel,
        ref ProjectionLatency.Signal last,
        ProjectionLatency.Signal signal)
    {
        if (last.Equals(signal) && panel.Children.Count > 0)
            return;
        last = signal;
        panel.Children.Clear();
        var color = HexBrush(signal.Tier switch
        {
            1 => "#22C55E",
            2 => "#EAB308",
            3 => "#EF4444",
            _ => "#6B7280"
        });
        var muted = HexBrush("#6B7280");
        var active = Math.Clamp(signal.Bars, 0, 4);
        double[] heights = [3, 5.5, 8, 11];
        for (var i = 0; i < 4; i++)
        {
            var on = i < active;
            panel.Children.Add(new Microsoft.UI.Xaml.Controls.Border
            {
                Width = 2.2,
                Height = heights[i],
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0.6),
                Background = on ? color : muted,
                Opacity = on ? 1 : 0.35,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom
            });
        }
    }

    public void CloseFromSession()
    {
        _closingFromSession = true;
        DetachCapture();
        ForceShowLocalCursor();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            // 全屏 Presenter 下直接 Close 经常关不掉，窗口会留在屏幕上。
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            appWindow.Hide();
        }
        catch { /* ignore */ }
        try { _window.Close(); } catch { /* ignore */ }
    }
}
