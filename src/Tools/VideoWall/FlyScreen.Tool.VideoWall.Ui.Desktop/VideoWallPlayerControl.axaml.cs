using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Avalonia;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public sealed partial class VideoWallPlayerControl : UserControl
{
    private const long SeekStepMs = 5_000;
    private const int SeekHoldMaxMs = 15_000;
    private const int SeekCatchupMs = 400;
    private const int SeekCatchupHoldMs = 400;
    private const double CueHitSize = 14;
    private const double CueDotSize = 6;
    private const double SeekBarMax = 1000;
    private const double CompactChromeThreshold = 300;
    private const double TitleChromeHeight = 56;
    private const double TransportChromeHeight = 128;
    private static readonly float[] SpeedSteps = [0.75f, 1f, 1.25f, 1.5f, 2f];

    private readonly DispatcherTimer _chromeHideTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _volumePopupHideTimer;
    private bool _hasPresentedFrame;

    private IVideoWallService? _service;
    private string? _pendingPlayId;
    private bool _volumeApplying;
    private bool _volumeDragging;
    private bool _seekDragging;
    private double? _seekPointerX;
    private long _seekHoldMs = -1;
    private long _seekHoldUntilTick;
    private long _seekHoldNearSinceTick = -1;
    private bool _volumePopupPinned;
    private bool _volumePopupAnimating;
    private int _speedIndex = 1;
    private FloatMode _floatMode;
    private Window? _floatWindow;
    private PixelRect _pipRestoreBounds;
    private Control? _floatPlaceholder;
    private CancellationTokenSource? _volumeFlyCts;
    private Popup? _lastClosedMenuPopup;
    private DateTime _lastMenuClosedUtc;
    private bool _suppressEpisodeSelection;
    private bool _suppressSwitch;
    private long _cueLengthMs = -1;
    private VideoWallFrameSurface? _frameSurface;

    private enum FloatMode { None, Pip, Maximized, Fullscreen }

    public Action<string>? StatusReporter { get; set; }
    public event Action? Closed;

    /// <summary>应用内最大化：由面板把当前源所在的墙格铺满整墙。</summary>
    public event Action? RequestInAppMaximize;

    public VideoWallPlayerControl()
    {
        AvaloniaIcon.RegisterTo(Resources);
        InitializeComponent();
        _chromeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _chromeHideTimer.Tick += (_, _) => HidePlaybackChrome();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => RefreshPlaybackProgress();
        _volumePopupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            if (!_volumePopupPinned && !_volumeDragging)
                _ = HideVolumePopupAnimatedAsync();
        };
        PlaybackVolumeBar.PropertyChanged += PlaybackVolumeBar_PropertyChanged;
        PlaybackVolumeBar.AddHandler(PointerPressedEvent, (_, _) =>
        {
            _volumeDragging = true;
            ShowVolumePopup();
        }, RoutingStrategies.Tunnel);
        PlaybackVolumeBar.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _volumeDragging = false;
            ScheduleHideVolumePopup();
        }, RoutingStrategies.Tunnel);
        PlaybackSeekBar.PropertyChanged += PlaybackSeekBar_PropertyChanged;
        PlaybackSeekBar.AddHandler(PointerReleasedEvent, PlaybackSeekBar_RightReleased, RoutingStrategies.Tunnel);
        ApplyLoc();
        SyncSpeedButton();
    }

    public void AttachService(IVideoWallService? service)
    {
        if (ReferenceEquals(_service, service)) return;
        DetachServiceSubscriptions();
        _service = service;
        if (_service is not null)
        {
            _service.SinglePlayback.StateChanged += OnPlaybackStateChanged;
            _service.SinglePlayback.StatsChanged += OnStatsChanged;
        }
    }

    public async Task PlayAsync(string sourceId, string title)
    {
        _pendingPlayId = sourceId;
        PlaybackTitle.Text = title;
        _cueLengthMs = -1;
        RefreshCueLayer();
        UpdateQualityUi(null);
        _hasPresentedFrame = false;
        SetLoadingOverlay(true, Loc.T("Tool.VideoWall.StatusConnecting"));
        ShowPlaybackChrome();
        BumpPlaybackChrome();
        StatusReporter?.Invoke(Loc.T("Tool.VideoWall.StatusConnecting"));
        if (_service is null) return;
        try
        {
            _frameSurface ??= new VideoWallFrameSurface(PlaybackFrameImage);
            _frameSurface.FramePresented -= OnFramePresented;
            _frameSurface.FramePresented += OnFramePresented;
            _frameSurface.Clear();
            await _service.SinglePlayback.StopAsync();
            await _service.SinglePlayback.PlaySourceAsync(
                sourceId, renderTarget: null, frameSink: _frameSurface);
            UpdateTransportChrome(_service.SinglePlayback.State);
            UpdateLoadingFromState(_service.SinglePlayback.State);
            _progressTimer.Start();
            StatusReporter?.Invoke(Loc.T("Tool.VideoWall.Play"));
        }
        catch (Exception ex)
        {
            _frameSurface?.Clear();
            _progressTimer.Stop();
            SetLoadingOverlay(false);
            StatusReporter?.Invoke(ex.Message);
        }
    }

    public async Task StopAsync()
    {
        _chromeHideTimer.Stop();
        _progressTimer.Stop();
        CloseMenuPopups();
        CloseFloatWindow(restoreUi: false);
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        if (_frameSurface is not null)
            _frameSurface.FramePresented -= OnFramePresented;
        _frameSurface?.Clear();
        _pendingPlayId = null;
        _hasPresentedFrame = false;
        PlaybackTitle.Text = "";
        PlaybackTitleDuration.Text = "";
        ResetSeekUi();
        SetLoadingOverlay(false);
        PlaybackTitleBarHost.IsVisible = false;
        PlaybackTransportBar.IsVisible = false;
        PlaybackVolumePopup.IsOpen = false;
        PlaybackVolumePopupPanel.Opacity = 0;
        if (PlaybackVolumePopupPanel.RenderTransform is TranslateTransform fly)
            fly.Y = 18;
        ApplyVideoChromeInset(title: false, transport: false);
    }

    private TranslateTransform VolumeFlyTransform =>
        PlaybackVolumePopupPanel.RenderTransform as TranslateTransform
        ?? throw new InvalidOperationException("PlaybackVolumePopupPanel missing TranslateTransform");

    private void DetachServiceSubscriptions()
    {
        if (_service is null) return;
        _service.SinglePlayback.StateChanged -= OnPlaybackStateChanged;
        _service.SinglePlayback.StatsChanged -= OnStatsChanged;
    }

    private void OnStatsChanged(MediaStats stats) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdateQualityUi(stats);
            if (stats.Width > 0 || stats.FirstFrameMs >= 0)
                MarkFramePresented();
        });

    private void OnFramePresented() => Dispatcher.UIThread.Post(MarkFramePresented);

    private void MarkFramePresented()
    {
        if (_hasPresentedFrame) return;
        _hasPresentedFrame = true;
        UpdateLoadingFromState(_service?.SinglePlayback.State ?? TilePlayState.Idle);
    }

    private void ApplyLoc()
    {
        ToolTip.SetTip(PlaybackBackBtn, Loc.T("Tool.VideoWall.Back"));
        ToolTip.SetTip(PlaybackMuteBtn, Loc.T("Tool.VideoWall.Mute"));
        ToolTip.SetTip(PlaybackSeekFwdBtn, Loc.T("Tool.VideoWall.SeekForward5"));
        ToolTip.SetTip(PlaybackQualityBtn, Loc.T("Tool.VideoWall.Quality"));
        PlaybackEpisodesBtn.Content = Loc.T("Tool.VideoWall.Episodes");
        ToolTip.SetTip(PlaybackEpisodesBtn, Loc.T("Tool.VideoWall.Episodes"));
        ToolTip.SetTip(PlaybackSettingsBtn, Loc.T("Tool.VideoWall.Settings"));
        ToolTip.SetTip(PlaybackPipBtn, Loc.T("Tool.VideoWall.PictureInPicture"));
        ToolTip.SetTip(PlaybackMaximizeBtn, Loc.T("Tool.VideoWall.Maximize"));
        PlaybackQualityTitle.Text = Loc.T("Tool.VideoWall.Quality");
        PlaybackQualityAutoText.Text = $"{Loc.T("Tool.VideoWall.QualityAuto")}  ✓";
        PlaybackEpisodesTitle.Text = Loc.T("Tool.VideoWall.Episodes");
        PlaybackSettingsTitle.Text = Loc.T("Tool.VideoWall.Settings");
        PlaybackLoopLabel.Text = Loc.T("Tool.VideoWall.Loop");
        PlaybackEpisodeTitleLabel.Text = Loc.T("Tool.VideoWall.EpisodeTitle");
        PlaybackAspectLabel.Text = Loc.T("Tool.VideoWall.Aspect");
        PlaybackAspectValue.Text = Loc.T("Tool.VideoWall.AspectFit");
        PlaybackDiagLabel.Text = Loc.T("Tool.VideoWall.Diagnostics");
        UpdateQualityUi(_service?.SinglePlayback.LastStats);
        ToolTip.SetTip(PlaybackPlayPauseBtn, Loc.T("Tool.VideoWall.Pause"));
        ToolTip.SetTip(PlaybackFullscreenBtn, Loc.T("Tool.VideoWall.Fullscreen"));
        ToolTip.SetTip(PlaybackVolumeBar, Loc.T("Tool.VideoWall.Volume"));
        ToolTip.SetTip(PlaybackSpeedBtn, Loc.T("Tool.VideoWall.Speed"));
        SyncSpeedButton();
    }

    private void OnPlaybackStateChanged(TilePlayState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTransportChrome(state);
            if (state is TilePlayState.Playing)
                _hasPresentedFrame = true;
            UpdateLoadingFromState(state);
        });

    private void UpdateLoadingFromState(TilePlayState state)
    {
        if (TilePlayStateUi.ShowsLoading(state, _hasPresentedFrame))
            SetLoadingOverlay(true, StatusTextFor(state));
        else
            SetLoadingOverlay(false);
    }

    private void SetLoadingOverlay(bool visible, string? text = null)
    {
        if (visible)
            PlaybackLoading.Show(string.IsNullOrWhiteSpace(text)
                ? Loc.T("Tool.VideoWall.StatusLoading")
                : text);
        else
            PlaybackLoading.Hide();
    }

    private static string StatusTextFor(TilePlayState state) => state switch
    {
        TilePlayState.Resolving => Loc.T("Tool.VideoWall.StatusResolving"),
        TilePlayState.Connecting => Loc.T("Tool.VideoWall.StatusConnecting"),
        TilePlayState.Buffering => Loc.T("Tool.VideoWall.StatusBuffering"),
        TilePlayState.Stalled => Loc.T("Tool.VideoWall.StatusStalled"),
        TilePlayState.Reconnecting => Loc.T("Tool.VideoWall.StatusReconnecting"),
        TilePlayState.Paused => Loc.T("Tool.VideoWall.Pause"),
        TilePlayState.Stopped => Loc.T("Tool.VideoWall.StatusStopped"),
        _ => state.ToString()
    };

    private async void PlaybackBackBtn_Click(object? sender, RoutedEventArgs e)
    {
        await StopAsync();
        Closed?.Invoke();
    }

    private void PlaybackPlayPause_Click(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause();
        BumpPlaybackChrome();
    }

    private void TogglePlayPause()
    {
        if (_service is null) return;
        var state = _service.SinglePlayback.State;
        if (state == TilePlayState.Paused)
            _service.SinglePlayback.Resume();
        else if (state is TilePlayState.Stopped or TilePlayState.Idle or TilePlayState.Failed)
        {
            var id = _pendingPlayId ?? _service.SinglePlayback.CurrentSourceId;
            if (!string.IsNullOrWhiteSpace(id))
                _ = PlayAsync(id, PlaybackTitle.Text ?? id);
        }
        else
            _service.SinglePlayback.Pause();
    }

    private void PlaybackSeekFwd_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.SeekBy(SeekStepMs);
        HoldSeekUi(_service.SinglePlayback.TimeMs);
        RefreshPlaybackProgress();
        BumpPlaybackChrome();
    }

    // ───────── 清晰度 / 选集 / 设置 弹层 ─────────

    private IEnumerable<Popup> MenuPopups()
    {
        yield return PlaybackQualityPopup;
        yield return PlaybackEpisodesPopup;
        yield return PlaybackSettingsPopup;
    }

    private bool AnyMenuPopupOpen() => MenuPopups().Any(p => p.IsOpen);

    private void CloseMenuPopups(Popup? except = null)
    {
        foreach (var p in MenuPopups())
        {
            if (!ReferenceEquals(p, except))
                p.IsOpen = false;
        }
    }

    private void ToggleMenuPopup(Popup popup)
    {
        // 轻触关闭会先于 Click 关掉弹层；同一弹层刚关就忽略这次 Click，避免立刻重开
        if (ReferenceEquals(_lastClosedMenuPopup, popup)
            && (DateTime.UtcNow - _lastMenuClosedUtc).TotalMilliseconds < 250)
            return;

        CloseMenuPopups(except: popup);
        PlaybackVolumePopup.IsOpen = false;
        popup.IsOpen = !popup.IsOpen;
        BumpPlaybackChrome();
    }

    private void MenuPopup_Closed(object? sender, EventArgs e)
    {
        _lastClosedMenuPopup = sender as Popup;
        _lastMenuClosedUtc = DateTime.UtcNow;
        BumpPlaybackChrome();
    }

    private void PlaybackQualityBtn_Click(object? sender, RoutedEventArgs e)
    {
        UpdateQualityUi(_service?.SinglePlayback.LastStats);
        ToggleMenuPopup(PlaybackQualityPopup);
    }

    private void UpdateQualityUi(MediaStats? stats)
    {
        if (stats is { Height: > 0, Width: > 0 })
        {
            PlaybackQualityBtn.Content = $"{stats.Height}P";
            PlaybackQualityCurrentText.Text = $"{stats.Width}×{stats.Height}";
            PlaybackQualityCurrentText.IsVisible = true;
        }
        else
        {
            PlaybackQualityBtn.Content = Loc.T("Tool.VideoWall.QualityAuto");
            PlaybackQualityCurrentText.Text = "";
            PlaybackQualityCurrentText.IsVisible = false;
        }
    }

    private void PlaybackEpisodesBtn_Click(object? sender, RoutedEventArgs e)
    {
        PopulateEpisodes();
        ToggleMenuPopup(PlaybackEpisodesPopup);
    }

    /// <summary>同分组的源即「集」；当前源无分组时列出全部源。</summary>
    private void PopulateEpisodes()
    {
        if (_service is null) return;
        var currentId = _service.SinglePlayback.CurrentSourceId ?? _pendingPlayId;
        var sources = _service.Catalog.Sources;
        var current = sources.FirstOrDefault(s => s.Id == currentId);
        IEnumerable<VideoSourceDescriptor> list = !string.IsNullOrWhiteSpace(current?.GroupId)
            ? sources.Where(s => string.Equals(s.GroupId, current!.GroupId, StringComparison.OrdinalIgnoreCase))
            : sources;

        _suppressEpisodeSelection = true;
        try
        {
            PlaybackEpisodesList.Items.Clear();
            foreach (var s in list)
            {
                var item = new ListBoxItem { Content = s.Name, Tag = s };
                PlaybackEpisodesList.Items.Add(item);
                if (s.Id == currentId)
                    PlaybackEpisodesList.SelectedItem = item;
            }
        }
        finally
        {
            _suppressEpisodeSelection = false;
        }
    }

    private void PlaybackEpisodesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEpisodeSelection || _service is null) return;
        if (PlaybackEpisodesList.SelectedItem is not ListBoxItem { Tag: VideoSourceDescriptor src }) return;

        PlaybackEpisodesPopup.IsOpen = false;
        if (src.Id == (_service.SinglePlayback.CurrentSourceId ?? _pendingPlayId)) return;
        _ = PlayAsync(src.Id, src.Name);
    }

    private void PlaybackSettingsBtn_Click(object? sender, RoutedEventArgs e)
    {
        _suppressSwitch = true;
        try { PlaybackLoopSwitch.IsChecked = _service?.SinglePlayback.Loop == true; }
        finally { _suppressSwitch = false; }
        RefreshDiagnosticsText();
        ToggleMenuPopup(PlaybackSettingsPopup);
    }

    private void PlaybackLoopSwitch_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressSwitch) return;
        _service?.SinglePlayback.SetLoop(PlaybackLoopSwitch.IsChecked == true);
        BumpPlaybackChrome();
    }

    private void PlaybackEpisodeTitleSwitch_Changed(object? sender, RoutedEventArgs e)
    {
        PlaybackTitleDuration.IsVisible = PlaybackEpisodeTitleSwitch.IsChecked == true;
        BumpPlaybackChrome();
    }

    private void PlaybackDiagRow_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        RefreshDiagnosticsText();
        PlaybackDiagText.IsVisible = !PlaybackDiagText.IsVisible;
    }

    private void RefreshDiagnosticsText()
    {
        var stats = _service?.SinglePlayback.LastStats;
        if (stats is null)
        {
            PlaybackDiagText.Text = Loc.T("Tool.VideoWall.DiagnosticsNone");
            return;
        }

        var decode = stats.HardwareDecode ? "HW" : Loc.T("Tool.VideoWall.SoftwareDecode");
        PlaybackDiagText.Text =
            $"{stats.Width}×{stats.Height} @ {stats.Fps:F1} fps\n" +
            $"{decode} · {stats.BufferMs} ms · {_service!.SinglePlayback.State}";
    }

    // ───────── 画中画 / 最大化 / 全屏（同一自定义浮窗） ─────────

    private void PlaybackMaximize_Click(object? sender, RoutedEventArgs e)
    {
        CloseMenuPopups();
        if (_floatMode == FloatMode.Maximized)
            ApplyFloatMode(FloatMode.Pip);
        else
            ApplyFloatMode(FloatMode.Maximized);
        BumpPlaybackChrome();
    }

    private void PlaybackPip_Click(object? sender, RoutedEventArgs e)
    {
        if (_floatMode != FloatMode.None)
            CloseFloatWindow();
        else
            ApplyFloatMode(FloatMode.Pip);
        BumpPlaybackChrome();
    }

    private void PlaybackFullscreen_Click(object? sender, RoutedEventArgs e)
    {
        CloseMenuPopups();
        if (_floatMode == FloatMode.Fullscreen)
            ApplyFloatMode(FloatMode.Pip);
        else
            ApplyFloatMode(FloatMode.Fullscreen);
        BumpPlaybackChrome();
    }

    private void ApplyFloatMode(FloatMode mode)
    {
        if (mode == FloatMode.None)
        {
            CloseFloatWindow();
            return;
        }

        EnsureFloatWindow();
        if (_floatWindow is null) return;

        if (mode == FloatMode.Pip)
        {
            _floatWindow.SystemDecorations = SystemDecorations.BorderOnly;
            _floatWindow.CanResize = true;
            _floatWindow.Topmost = true;
            if (_floatMode is FloatMode.Maximized or FloatMode.Fullscreen
                && _pipRestoreBounds.Width > 0 && _pipRestoreBounds.Height > 0)
            {
                _floatWindow.WindowState = WindowState.Normal;
                _floatWindow.Position = new PixelPoint(_pipRestoreBounds.X, _pipRestoreBounds.Y);
                _floatWindow.Width = _pipRestoreBounds.Width;
                _floatWindow.Height = _pipRestoreBounds.Height;
            }
            else
            {
                PlacePipWindow(_floatWindow);
                _floatWindow.WindowState = WindowState.Normal;
            }
        }
        else if (mode == FloatMode.Maximized)
        {
            RememberPipBounds();
            _floatWindow.SystemDecorations = SystemDecorations.BorderOnly;
            _floatWindow.CanResize = true;
            _floatWindow.Topmost = true;
            _floatWindow.WindowState = WindowState.Maximized;
        }
        else
        {
            RememberPipBounds();
            _floatWindow.SystemDecorations = SystemDecorations.None;
            _floatWindow.CanResize = false;
            _floatWindow.Topmost = true;
            _floatWindow.WindowState = WindowState.FullScreen;
        }

        _floatMode = mode;
        UpdateFloatChrome();
        ShowPlaybackChrome();
        _floatWindow.Activate();
    }

    private void EnsureFloatWindow()
    {
        if (_floatWindow is not null) return;
        CloseMenuPopups();

        var title = string.IsNullOrWhiteSpace(PlaybackTitle.Text)
            ? Loc.T("Tool.VideoWall.Name")
            : PlaybackTitle.Text!;
        var win = new Window
        {
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            CanResize = true,
            SystemDecorations = SystemDecorations.BorderOnly,
            Title = $"{title} · {Loc.T("Tool.VideoWall.PictureInPicture")}"
        };
        PlacePipWindow(win);

        // 把整棵 PlaybackRoot 迁入浮窗（含软件帧与 chrome）
        Content = _floatPlaceholder ??= new Border { Background = Brushes.Black, MinHeight = 200 };
        win.Content = PlaybackRoot;

        win.KeyDown += FloatWindow_KeyDown;
        win.PointerMoved += FloatWindow_PointerMoved;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_floatWindow, win))
                CloseFloatWindow(restoreUi: true);
        };

        _floatWindow = win;
        win.Show();
        HookTitleBarDrag(true);
    }

    private void PlacePipWindow(Window win)
    {
        var (w, h) = VideoWallDisplayPlacement.DefaultPipSize;
        var pos = VideoWallDisplayPlacement.DefaultPipTopLeft(this);
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Width = w;
        win.Height = h;
        win.Position = pos;
        _pipRestoreBounds = new PixelRect(pos.X, pos.Y, (int)w, (int)h);
    }

    private void RememberPipBounds()
    {
        if (_floatWindow is null || _floatMode != FloatMode.Pip) return;
        if (_floatWindow.WindowState != WindowState.Normal) return;
        var pos = _floatWindow.Position;
        _pipRestoreBounds = new PixelRect(pos.X, pos.Y, (int)_floatWindow.Width, (int)_floatWindow.Height);
    }

    private void CloseFloatWindow(bool restoreUi = true)
    {
        HookTitleBarDrag(false);
        _chromeHideTimer.Stop();
        var win = _floatWindow;
        _floatWindow = null;
        var had = _floatMode != FloatMode.None;
        _floatMode = FloatMode.None;

        if (win is not null)
        {
            win.KeyDown -= FloatWindow_KeyDown;
            win.PointerMoved -= FloatWindow_PointerMoved;
            win.Content = null;
            try { win.Close(); } catch { /* ignore */ }
            Content = PlaybackRoot;
        }

        if (restoreUi && had)
            ShowPlaybackChrome();

        UpdateFloatChrome();
    }

    private void HookTitleBarDrag(bool on)
    {
        PlaybackTitleBar.PointerPressed -= PlaybackTitleBar_PointerPressed;
        if (on)
            PlaybackTitleBar.PointerPressed += PlaybackTitleBar_PointerPressed;
    }

    private void PlaybackTitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_floatMode != FloatMode.Pip || _floatWindow is null) return;
        if (!e.GetCurrentPoint(PlaybackTitleBar).Properties.IsLeftButtonPressed) return;
        try { _floatWindow.BeginMoveDrag(e); } catch { /* ignore */ }
        RememberPipBounds();
    }

    private void FloatWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_floatMode is FloatMode.Maximized or FloatMode.Fullscreen)
                ApplyFloatMode(FloatMode.Pip);
            else
                CloseFloatWindow();
            e.Handled = true;
        }
        else BumpPlaybackChrome();
    }

    private void FloatWindow_PointerMoved(object? sender, PointerEventArgs e) => BumpPlaybackChrome();

    private void UpdateFloatChrome()
    {
        ToolTip.SetTip(PlaybackPipBtn, Loc.T(
            _floatMode != FloatMode.None ? "Tool.VideoWall.PictureInPictureExit" : "Tool.VideoWall.PictureInPicture"));
        ToolTip.SetTip(PlaybackMaximizeBtn, Loc.T(
            _floatMode == FloatMode.Maximized ? "Tool.VideoWall.MaximizeExit" : "Tool.VideoWall.Maximize"));
        PlaybackFullscreenIcon.Data = _floatMode == FloatMode.Fullscreen
            ? (Geometry?)Resources["GeoFullscreenExit"]
            : (Geometry?)Resources["GeoFullscreen"];
        ToolTip.SetTip(PlaybackFullscreenBtn, Loc.T(
            _floatMode == FloatMode.Fullscreen ? "Tool.VideoWall.FullscreenExit" : "Tool.VideoWall.Fullscreen"));
    }

    private void PlaybackSpeedBtn_Click(object? sender, RoutedEventArgs e)
    {
        _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
        var rate = SpeedSteps[_speedIndex];
        _service?.SinglePlayback.SetRate(rate);
        SyncSpeedButton();
        BumpPlaybackChrome();
    }

    private void PlaybackMute_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.ToggleMute();
        SyncVolumeUi();
        ShowVolumePopup();
        BumpPlaybackChrome();
    }

    private void PlaybackVolumeHost_PointerEntered(object? sender, PointerEventArgs e)
    {
        _volumePopupPinned = true;
        ShowVolumePopup();
        BumpPlaybackChrome();
    }

    private void PlaybackVolumeHost_PointerExited(object? sender, PointerEventArgs e)
    {
        _volumePopupPinned = false;
        ScheduleHideVolumePopup();
    }

    private void PlaybackVolumePopup_PointerEntered(object? sender, PointerEventArgs e)
    {
        _volumePopupPinned = true;
        _volumePopupHideTimer.Stop();
        BumpPlaybackChrome();
    }

    private void PlaybackVolumePopup_PointerExited(object? sender, PointerEventArgs e)
    {
        _volumePopupPinned = false;
        ScheduleHideVolumePopup();
    }

    private void ShowVolumePopup()
    {
        _volumePopupHideTimer.Stop();
        SyncVolumeUi();
        PlaybackVolumePopup.PlacementTarget = PlaybackVolumeHost;
        PlaybackVolumePopup.Placement = PlacementMode.Top;
        if (PlaybackVolumePopup.IsOpen && !_volumePopupAnimating
            && PlaybackVolumePopupPanel.Opacity >= 0.99)
            return;

        _volumeFlyCts?.Cancel();
        _volumeFlyCts = new CancellationTokenSource();
        var ct = _volumeFlyCts.Token;
        PlaybackVolumePopup.IsOpen = true;
        _volumePopupAnimating = true;
        _ = AnimateVolumePopupAsync(show: true, ct);
    }

    private async Task HideVolumePopupAnimatedAsync()
    {
        if (!PlaybackVolumePopup.IsOpen) return;
        _volumeFlyCts?.Cancel();
        _volumeFlyCts = new CancellationTokenSource();
        var ct = _volumeFlyCts.Token;
        _volumePopupAnimating = true;
        await AnimateVolumePopupAsync(show: false, ct);
        if (ct.IsCancellationRequested || _volumePopupPinned || _volumeDragging) return;
        PlaybackVolumePopup.IsOpen = false;
    }

    private async Task AnimateVolumePopupAsync(bool show, CancellationToken ct)
    {
        const int frames = 10;
        const int frameMs = 16;
        var fly = VolumeFlyTransform;
        var fromOp = PlaybackVolumePopupPanel.Opacity;
        var fromY = fly.Y;
        var toOp = show ? 1.0 : 0.0;
        var toY = show ? 0.0 : 18.0;
        try
        {
            for (var i = 1; i <= frames; i++)
            {
                if (ct.IsCancellationRequested) return;
                var t = i / (double)frames;
                // ease-out / ease-in
                var e = show ? 1 - Math.Pow(1 - t, 2) : Math.Pow(t, 2);
                PlaybackVolumePopupPanel.Opacity = fromOp + (toOp - fromOp) * e;
                fly.Y = fromY + (toY - fromY) * e;
                await Task.Delay(frameMs, ct);
            }
        }
        catch (TaskCanceledException) { /* ignore */ }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                PlaybackVolumePopupPanel.Opacity = toOp;
                fly.Y = toY;
                _volumePopupAnimating = false;
            }
        }
    }

    private void ScheduleHideVolumePopup()
    {
        if (_volumeDragging || _volumePopupPinned) return;
        _volumePopupHideTimer.Stop();
        _volumePopupHideTimer.Start();
    }

    private void PlaybackVolumeBar_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty || _volumeApplying || _service is null) return;
        var vol = (int)Math.Round(PlaybackVolumeBar.Value);
        _service.SinglePlayback.SetVolume(vol);
        PlaybackVolumePct.Text = $"{vol}%";
        SyncMuteLabel();
        BumpPlaybackChrome();
    }

    private void SyncVolumeUi()
    {
        if (_service is null) return;
        _volumeApplying = true;
        try
        {
            PlaybackVolumeBar.Value = _service.SinglePlayback.Volume;
            PlaybackVolumePct.Text = $"{_service.SinglePlayback.Volume}%";
        }
        finally { _volumeApplying = false; }
        SyncMuteLabel();
    }

    private void SyncMuteLabel()
    {
        if (_service is null) return;
        var muted = _service.SinglePlayback.IsMuted;
        PlaybackMuteIcon.Data = muted
            ? (Geometry?)Resources["GeoMute"]
            : (Geometry?)Resources["GeoVolume"];
        ToolTip.SetTip(PlaybackMuteBtn, Loc.T(muted ? "Tool.VideoWall.Unmute" : "Tool.VideoWall.Mute"));
    }

    private void SyncSpeedButton()
    {
        var rate = SpeedSteps[Math.Clamp(_speedIndex, 0, SpeedSteps.Length - 1)];
        PlaybackSpeedBtn.Content = Math.Abs(rate - 1f) < 0.01f
            ? Loc.T("Tool.VideoWall.Speed")
            : $"{rate:0.##}x";
    }

    private void Root_PointerMoved(object? sender, PointerEventArgs e) => BumpPlaybackChrome();

    private void Root_PointerEntered(object? sender, PointerEventArgs e) => BumpPlaybackChrome();

    private void Root_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_seekDragging || AnyMenuPopupOpen()) return;
        if (e.Source is Button or Slider or ToggleButton or ListBox or TextBox) return;
        // 点在按钮/滑块子视觉上时 Source 可能是 Path/TextBlock
        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is Button or Slider or ToggleButton or ListBox or TextBox or Popup)
                return;
        }

        TogglePlayPause();
        BumpPlaybackChrome();
        e.Handled = true;
    }

    private void Root_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!PlaybackTitleBarHost.IsVisible && !PlaybackTransportBar.IsVisible) return;
        ShowPlaybackChrome();
    }

    private void BumpPlaybackChrome()
    {
        if (!IsVisible && _floatMode == FloatMode.None) return;
        ShowPlaybackChrome();
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    private void ShowPlaybackChrome()
    {
        var showTitle = _floatMode != FloatMode.Fullscreen;
        var showTransport = _floatMode == FloatMode.Fullscreen || !IsCompactChrome();
        PlaybackTitleBarHost.IsVisible = showTitle;
        PlaybackTransportBar.IsVisible = showTransport;
        ApplyVideoChromeInset(title: showTitle, transport: showTransport);
    }

    private void HidePlaybackChrome()
    {
        _chromeHideTimer.Stop();
        if ((!IsVisible && _floatMode == FloatMode.None) || _seekDragging || AnyMenuPopupOpen()) return;
        _ = HideVolumePopupAnimatedAsync();

        if (_floatMode == FloatMode.Maximized)
        {
            PlaybackTitleBarHost.IsVisible = true;
            PlaybackTransportBar.IsVisible = false;
            ApplyVideoChromeInset(title: true, transport: false);
            return;
        }

        PlaybackTitleBarHost.IsVisible = false;
        PlaybackTransportBar.IsVisible = false;
        ApplyVideoChromeInset(title: false, transport: false);
    }

    private bool IsCompactChrome()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (_floatMode != FloatMode.None && _floatWindow is not null)
        {
            w = _floatWindow.Width;
            h = _floatWindow.Height;
        }

        return w > 0 && h > 0 && (w < CompactChromeThreshold || h < CompactChromeThreshold);
    }

    private void ApplyVideoChromeInset(bool title, bool transport)
    {
        // 软件帧：chrome 叠在画面上，不 inset
        _ = title;
        _ = transport;
        PlaybackVideoHost.Margin = new Thickness(0);
    }

    private void PlaybackSeekBar_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty || !_seekDragging || _service is null) return;
        var len = ResolveSeekLengthMs();
        if (len <= 0) return;
        var ms = (long)(PlaybackSeekBar.Value / SeekBarMax * len);
        PlaybackTimeText.Text = FormatClock(ms);
    }

    private void PlaybackSeekBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PlaybackSeekBar).Properties.IsLeftButtonPressed) return;
        _seekDragging = true;
        _seekPointerX = e.GetPosition(PlaybackSeekBar).X;
        // 按下即 Seek；拖动中只预览，松手再提交最终目标
        ApplySeekFromPointerX(_seekPointerX.Value);
        e.Pointer.Capture(PlaybackSeekBar);
        e.Handled = true;
    }

    private void PlaybackSeekBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seekDragging) return;
        _seekPointerX = e.GetPosition(PlaybackSeekBar).X;
        PreviewSeekFromPointerX(_seekPointerX.Value);
    }

    private void PlaybackSeekBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        EndSeekDrag(e.GetPosition(PlaybackSeekBar).X);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndSeekDrag(double? pointerX)
    {
        if (!_seekDragging) return;
        if (pointerX is double x)
            ApplySeekFromPointerX(x);
        else
            ApplySeekFromBar();
        _seekDragging = false;
        _seekPointerX = null;
        BumpPlaybackChrome();
    }

    private long ResolveSeekLengthMs()
    {
        var len = _service?.SinglePlayback.LengthMs ?? 0;
        if (len <= 0) len = _cueLengthMs;
        return len;
    }

    private void PreviewSeekFromPointerX(double x)
    {
        var len = ResolveSeekLengthMs();
        if (len <= 0) return;
        var ratio = PointerXToRatio(x);
        PlaybackSeekBar.Value = ratio * SeekBarMax;
        HoldSeekUi((long)(ratio * len));
    }

    private void ApplySeekFromPointerX(double x)
    {
        if (_service is null) return;
        var len = ResolveSeekLengthMs();
        if (len <= 0) return;
        var ratio = PointerXToRatio(x);
        var ms = (long)(ratio * len);
        PlaybackSeekBar.Value = ratio * SeekBarMax;
        HoldSeekUi(ms);
        _service.SinglePlayback.SeekTo(ms);
    }

    private double PointerXToRatio(double x)
    {
        var thumb = SeekThumbWidth();
        var w = Math.Max(1, PlaybackSeekBar.Bounds.Width);
        return Math.Clamp((x - thumb / 2) / Math.Max(1, w - thumb), 0, 1);
    }

    private void ApplySeekFromBar()
    {
        if (_service is null) return;
        var len = ResolveSeekLengthMs();
        if (len <= 0) return;
        var ms = (long)(PlaybackSeekBar.Value / SeekBarMax * len);
        HoldSeekUi(ms);
        _service.SinglePlayback.SeekTo(ms);
    }

    private void HoldSeekUi(long ms)
    {
        _seekHoldMs = ms;
        _seekHoldUntilTick = Environment.TickCount64 + SeekHoldMaxMs;
        _seekHoldNearSinceTick = -1;
        PlaybackTimeText.Text = FormatClock(ms);
    }

    private void RefreshPlaybackProgress()
    {
        if (_service is null || _seekDragging) return;
        var time = _service.SinglePlayback.TimeMs;
        var len = _service.SinglePlayback.LengthMs;
        if (_seekHoldMs >= 0)
        {
            var now = Environment.TickCount64;
            if (Math.Abs(time - _seekHoldMs) <= SeekCatchupMs)
            {
                if (_seekHoldNearSinceTick < 0)
                    _seekHoldNearSinceTick = now;
                else if (now - _seekHoldNearSinceTick >= SeekCatchupHoldMs)
                    _seekHoldMs = -1;
            }
            else
                _seekHoldNearSinceTick = -1;

            if (_seekHoldMs >= 0)
                time = _seekHoldMs;
        }

        PlaybackTimeText.Text = FormatClock(time);
        PlaybackLengthText.Text = len > 0 ? FormatClock(len) : "--:--";
        PlaybackTitleDuration.Text = len > 0 ? FormatClock(len) : "";
        PlaybackSeekBar.IsEnabled = len > 0;
        if (len > 0)
            PlaybackSeekBar.Value = Math.Clamp(time * SeekBarMax / len, 0, SeekBarMax);
        if (len != _cueLengthMs)
            RefreshCueLayer();
    }

    private void ResetSeekUi()
    {
        _seekHoldMs = -1;
        _seekHoldNearSinceTick = -1;
        PlaybackTimeText.Text = "00:00";
        PlaybackLengthText.Text = "00:00";
        PlaybackSeekBar.Value = 0;
        PlaybackTitleDuration.Text = "";
        _cueLengthMs = -1;
        PlaybackCueLayer.Children.Clear();
    }

    // ───────── 关键事件白点 ─────────

    private void PlaybackSeekBar_SizeChanged(object? sender, SizeChangedEventArgs e) => RefreshCueLayer();

    private double SeekThumbWidth()
    {
        var w = PlaybackSeekBar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault()?.Bounds.Width ?? 0;
        return w > 0 ? w : 20;
    }

    private double CueXFor(long ms, long len)
    {
        var thumb = SeekThumbWidth();
        var ratio = Math.Clamp((double)ms / len, 0, 1);
        return thumb / 2 + ratio * Math.Max(0, PlaybackSeekBar.Bounds.Width - thumb);
    }

    /// <summary>按当前源 KeyEvents 与总时长重绘白点；无总时长（直播）时不画。</summary>
    private void RefreshCueLayer()
    {
        PlaybackCueLayer.Children.Clear();
        var len = _service?.SinglePlayback.LengthMs ?? 0;
        _cueLengthMs = len;
        if (_service is null || len <= 0) return;

        foreach (var cue in _service.SinglePlayback.GetKeyEvents())
        {
            if (cue.PositionMs < 0 || cue.PositionMs > len) continue;

            var hit = new Border
            {
                Width = CueHitSize,
                Height = CueHitSize,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = cue,
                Child = new Ellipse
                {
                    Width = CueDotSize,
                    Height = CueDotSize,
                    Fill = Brushes.White,
                    IsHitTestVisible = false
                }
            };
            ToolTip.SetTip(hit, string.IsNullOrWhiteSpace(cue.Label)
                ? FormatClock(cue.PositionMs)
                : $"{FormatClock(cue.PositionMs)}  {cue.Label}");

            var remove = new MenuItem { Header = Loc.T("Tool.VideoWall.RemoveCue") };
            var captured = cue;
            remove.Click += async (_, _) => await RemoveCueAsync(captured);
            hit.ContextMenu = new ContextMenu { Items = { remove } };
            hit.PointerPressed += Cue_PointerPressed;

            Canvas.SetLeft(hit, CueXFor(cue.PositionMs, len) - CueHitSize / 2);
            Canvas.SetTop(hit, (PlaybackCueLayer.Height - CueHitSize) / 2);
            PlaybackCueLayer.Children.Add(hit);
        }
    }

    private void Cue_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_service is null || sender is not Control { Tag: PlaybackCue cue } c) return;
        if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (!_service.SinglePlayback.CanSeek) return;
        _service.SinglePlayback.SeekTo(cue.PositionMs);
        HoldSeekUi(cue.PositionMs);
        RefreshPlaybackProgress();
        BumpPlaybackChrome();
    }

    private void PlaybackSeekBar_RightReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || _service is null) return;
        e.Handled = true;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0) return;

        var thumb = SeekThumbWidth();
        var x = e.GetPosition(PlaybackSeekBar).X;
        var ratio = Math.Clamp((x - thumb / 2) / Math.Max(1, PlaybackSeekBar.Bounds.Width - thumb), 0, 1);
        _ = AddCueAsync((long)(ratio * len));
    }

    private async Task AddCueAsync(long positionMs)
    {
        if (_service is null) return;
        if (string.IsNullOrWhiteSpace(_service.SinglePlayback.CurrentSourceId))
        {
            StatusReporter?.Invoke(Loc.T("Tool.VideoWall.CueNoSource"));
            return;
        }

        var current = _service.SinglePlayback.GetKeyEvents();
        // 同一位置 1 秒内视为重复
        if (current.Any(c => Math.Abs(c.PositionMs - positionMs) < 1000)) return;

        if (!await _service.SinglePlayback.SetKeyEventsAsync(current.Append(new PlaybackCue(positionMs))))
            return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshCueLayer();
            StatusReporter?.Invoke($"{Loc.T("Tool.VideoWall.AddCue")}: {FormatClock(positionMs)}");
        });
    }

    private async Task RemoveCueAsync(PlaybackCue cue)
    {
        if (_service is null) return;
        var next = _service.SinglePlayback.GetKeyEvents().Where(c => c != cue);
        if (!await _service.SinglePlayback.SetKeyEventsAsync(next)) return;
        await Dispatcher.UIThread.InvokeAsync(RefreshCueLayer);
    }

    private void UpdateTransportChrome(TilePlayState state)
    {
        var pausedOrStopped = state is TilePlayState.Paused or TilePlayState.Stopped
            or TilePlayState.Idle or TilePlayState.Failed;
        PlaybackPlayPauseIcon.Data = pausedOrStopped
            ? (Geometry?)Resources["GeoPlay"]
            : (Geometry?)Resources["GeoPause"];
        ToolTip.SetTip(PlaybackPlayPauseBtn, Loc.T(pausedOrStopped
            ? state == TilePlayState.Paused ? "Tool.VideoWall.Resume" : "Tool.VideoWall.Play"
            : "Tool.VideoWall.Pause"));
        SyncVolumeUi();
        RefreshPlaybackProgress();
    }

    private static string FormatClock(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
