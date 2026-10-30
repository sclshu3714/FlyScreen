using FlyScreen.Native;
using FlyScreen.ScreenExtend;
using FlyScreen.Services.ScreenExtend;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
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

    public MauiScreenExtendController(ScreenExtendService service)
    {
        _service = service;
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

    private void OnHostReady()
    {
        _hostSessionActive = true;
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

                try
                {
                    var frame = ScreenCaptureJpeg.Capture(target.Value);
                    if (frame is { Jpeg.Length: > 0 })
                    {
                        await _service.SendFrameAsync(frame.Value.Jpeg, frame.Value.Width, frame.Value.Height, token)
                            .ConfigureAwait(false);
                    }
                }
                catch { /* ignore */ }

                try { await Task.Delay(66, token).ConfigureAwait(false); }
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

    private void OnSinkReady()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _sink ??= new MauiScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
            _sink.ShowFullscreen();
        });
    }

    private void OnFrame(ReadOnlyMemory<byte> jpeg, int w, int h)
    {
        var copy = jpeg.ToArray();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _sink ??= new MauiScreenExtendSinkWindow(() => _ = _service.StopAsync("sink-close"));
                if (!_sink.IsVisible)
                    _sink.ShowFullscreen();
                _sink.PresentJpeg(copy);
            }
            catch { /* ignore */ }
        });
    }

    private void OnSessionEnded()
    {
        var releaseHost = _hostSessionActive || _wantDisplay || _vdd != null;
        _hostSessionActive = false;
        _wantDisplay = false;
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
    private readonly Microsoft.UI.Xaml.Window _window;
    private readonly Microsoft.UI.Xaml.Controls.Image _image;
    private readonly Microsoft.UI.Xaml.Controls.TextBlock _waiting;
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private int _showCursorHideDepth;

    public MauiScreenExtendSinkWindow(Action onUserClose)
    {
        _onUserClose = onUserClose;
        _image = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
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
        var root = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
        };
        root.Children.Add(_image);
        root.Children.Add(_waiting);
        var tip = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Esc 退出投影",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            Opacity = 0.55,
            Margin = new Microsoft.UI.Xaml.Thickness(16),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top
        };
        root.Children.Add(tip);
        root.IsTabStop = true;
        root.KeyDown += Root_KeyDown;
        root.PointerMoved += (_, _) => _cursorIdle.NotifyLocalMouseMoved();
        _cursorIdle.ShowLocalCursorChanged += ApplyLocalCursor;

        _window = new Microsoft.UI.Xaml.Window { Title = "玄鸟投影", Content = root };
        _window.Closed += (_, _) =>
        {
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
        }
    }

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
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
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
    }

    public async void PresentJpeg(byte[] jpeg)
    {
        try
        {
            var ra = new InMemoryRandomAccessStream();
            var writer = new DataWriter(ra);
            writer.WriteBytes(jpeg);
            await writer.StoreAsync();
            writer.DetachStream();
            writer.Dispose();
            ra.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ra);
            _image.Source = bmp;
            _waiting.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        catch { /* ignore */ }
    }

    public void CloseFromSession()
    {
        _closingFromSession = true;
        ForceShowLocalCursor();
        try { _window.Close(); } catch { /* ignore */ }
    }
}
