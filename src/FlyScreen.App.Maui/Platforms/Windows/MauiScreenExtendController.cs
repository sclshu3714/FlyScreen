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
    private readonly LatestFrameSlot _frames = new();
    private int _decodePosted;

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
                    var picture = ProjectionDisplayPresets.DescribeQuality(display.JpegQuality);
                    var maxEdge = picture.Passthrough ? 0 : Math.Max(display.Width, display.Height);
                    if (!picture.Passthrough && picture.ExtraLongEdgeCap > 0)
                        maxEdge = Math.Min(maxEdge, picture.ExtraLongEdgeCap);
                    var frame = ScreenCaptureJpeg.Capture(target.Value, picture.EncodeQuality, maxEdge);
                    if (frame is { Jpeg.Length: > 0 })
                    {
                        await _service.SendFrameAsync(frame.Value.Jpeg, frame.Value.Width, frame.Value.Height, token)
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
        if (_mode == DisplayProjectionMode.Clone)
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

    private void OnSinkReady()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _sink ??= new MauiScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
            _sink.BindSession(_service);
            _sink.ShowFullscreen();
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
        _ = DecodeLoopAsync();
    }

    private async Task DecodeLoopAsync()
    {
        try
        {
            while (!_disposed && _frames.TryTake(out var jpeg, out _, out _, out var gen))
            {
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
                if (!_frames.IsCurrent(gen) || _service.Role != ScreenExtendRole.Sink)
                    continue;

                var shown = frame.Value;
                var g = gen;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (_disposed || _service.Role != ScreenExtendRole.Sink || !_frames.IsCurrent(g))
                        return;

                    try
                    {
                        _sink ??= new MauiScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
                        _sink.BindSession(_service);
                        if (!_sink.IsVisible)
                            _sink.ShowFullscreen();
                        _sink.PresentBgra(shown.Width, shown.Height, shown.Pixels);
                    }
                    catch { /* ignore */ }
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

    private void OnSessionEnded()
    {
        var releaseHost = _hostSessionActive || _wantDisplay || _vdd != null;
        _hostSessionActive = false;
        _wantDisplay = false;
        _frames.Clear();
        _ = FinalizeAfterSessionEndedAsync(releaseHost);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { _sink?.CloseFromSession(); } catch { /* ignore */ }
            _sink = null;
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
        _ = FinalizeAfterSessionEndedAsync(releaseHost);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { _sink?.CloseFromSession(); } catch { /* ignore */ }
            _sink = null;
            MauiProjectionTitleBar.SetProjecting(false);
        });
    }
}

/// <summary>WinUI 全屏从机窗。</summary>
public sealed class MauiScreenExtendSinkWindow
{
    private readonly Action _onUserClose;
    private readonly SinkLocalCursorIdleHide _cursorIdle = new();
    private readonly ScreenExtendReverseMoveGate _reverseGate = new();
    private readonly Microsoft.UI.Xaml.Window _window;
    private readonly Microsoft.UI.Xaml.Controls.Grid _root;
    private readonly Microsoft.UI.Xaml.Controls.Image _image;
    private readonly Microsoft.UI.Xaml.Controls.TextBlock _waiting;
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private int _showCursorHideDepth;
    private ScreenExtendService? _session;
    private bool _captureBound;
    private bool _windowActive;
    private int _contentW;
    private int _contentH;

    public MauiScreenExtendSinkWindow(Action onUserClose)
    {
        _onUserClose = onUserClose;
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
        _root = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
        };
        // Viewbox 按窗口等比放大/缩小。Image 自身在部分 WinUI 版本上会停在像素尺寸，高和宽都填不满。
        _root.Children.Add(new Microsoft.UI.Xaml.Controls.Viewbox
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            StretchDirection = Microsoft.UI.Xaml.Controls.StretchDirection.Both,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
            Child = _image
        });
        _root.Children.Add(_waiting);
        var tip = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Esc 退出投影",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            Opacity = 0.55,
            Margin = new Microsoft.UI.Xaml.Thickness(16),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top
        };
        _root.Children.Add(tip);
        _root.IsTabStop = true;
        _root.KeyDown += Root_KeyDown;
        _root.KeyUp += Root_KeyUp;
        _root.PointerMoved += (_, _) => _cursorIdle.NotifyLocalMouseMoved();
        _cursorIdle.ShowLocalCursorChanged += ApplyLocalCursor;

        _window = new Microsoft.UI.Xaml.Window { Title = "玄鸟投影", Content = _root };
        _window.Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState
                != Microsoft.UI.Xaml.WindowActivationState.Deactivated;
            if (_windowActive)
            {
                try { _root.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { /* ignore */ }
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
            NotifyUserClose();
        };
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.Closing += (_, args) =>
            {
                if (_closingFromSession) return;
                NotifyUserClose();
            };
        }
        catch { /* Activate 后再绑 Closing 亦可 */ }
    }

    public void BindSession(ScreenExtendService service)
    {
        _session = service;
        if (_windowActive)
            TryAttachCapture();
        else
            DetachCapture();
    }

    private void TryAttachCapture()
    {
        if (_captureBound) return;
        if (_session?.CanSendReverseInput != true) return;
        _captureBound = true;
        _reverseGate.OnCaptureAttached();
        _root.PointerMoved += OnPointerMoved;
        _root.PointerPressed += OnPointerPressed;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerWheelChanged += OnPointerWheel;
    }

    private void DetachCapture()
    {
        if (!_captureBound) return;
        _captureBound = false;
        _reverseGate.OnCaptureDetached();
        _root.PointerMoved -= OnPointerMoved;
        _root.PointerPressed -= OnPointerPressed;
        _root.PointerReleased -= OnPointerReleased;
        _root.PointerWheelChanged -= OnPointerWheel;
    }

    private bool TryMap(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, out double nx, out double ny)
    {
        var pt = e.GetCurrentPoint(_root).Position;
        return ScreenExtendReverseInput.TryMapUniformContent(
            _root.ActualWidth, _root.ActualHeight, _contentW, _contentH, pt.X, pt.Y, out nx, out ny);
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
        var props = e.GetCurrentPoint(_root).Properties;
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

    private void NotifyUserClose()
    {
        if (_closingFromSession || _userCloseRaised) return;
        _userCloseRaised = true;
        try { _onUserClose(); } catch { /* ignore */ }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            NotifyUserClose();
            try { _window.Close(); } catch { /* ignore */ }
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
        _cursorIdle.BeginSession();
        ForceHideLocalCursor();
        _window.Activate();
        try { _root.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { /* ignore */ }
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (area != null)
                appWindow.MoveAndResize(area.OuterBounds);
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            appWindow.Show();
        }
        catch
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                    op.Maximize();
                appWindow.Show();
            }
            catch
            {
                _window.Activate();
            }
        }

        _windowActive = true;
        TryAttachCapture();
    }

    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _bitmap;

    public void PresentBgra(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * 4 * height)
            return;

        _contentW = width;
        _contentH = height;
        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
            _image.Source = _bitmap;
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Position = 0;
            stream.Write(pixels, 0, width * 4 * height);
        }

        _bitmap.Invalidate();
        _waiting.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
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
