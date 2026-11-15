using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public sealed partial class VideoWallPlayerControl : UserControl
{
    private const long SeekStepMs = 5_000;
    private const double SeekBarMax = 1000;
    private const double SeekThumbWidth = 10;
    private const int SeekHoldMaxMs = 15_000;
    private const int SeekCatchupMs = 400;
    private const int SeekCatchupHoldMs = 400;
    private long _seekHoldNearSinceTick = -1;
    private const double CueHitSize = 14;
    private const double CueDotSize = 6;
    private const double CompactChromeThreshold = 500;
    private const double TitleChromeHeight = 56;
    private const double TransportChromeHeight = 128;
    private static readonly float[] SpeedSteps = [0.75f, 1f, 1.25f, 1.5f, 2f];

    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _chromeHideTimer;
    private static readonly Brush TitleChromeBrush = CreateTitleChromeBrush();
    private static readonly Brush TransportChromeBrush = CreateTransportChromeBrush();

    private IVideoWallService? _service;
    private string? _pendingPlayId;
    private bool _seekDragging;
    private double? _seekPointerX;
    private long _seekHoldMs = -1;
    private long _seekHoldUntilTick;
    private bool _volumeDragging;
    private bool _fullscreen;
    private bool _chromeTitleVisible;
    private bool _chromeTransportVisible;
    private bool _volumePopupPinned;
    private int _speedIndex = 1;
    private Window? _fullscreenWindow;
    private bool _pip;
    private Window? _pipWindow;
    private readonly DispatcherTimer _volumePopupHideTimer;
    private Popup? _lastClosedMenuPopup;
    private DateTime _lastMenuClosedUtc;
    private bool _suppressEpisodeSelection;
    private long _cueLengthMs = -1;
    private VideoWallFrameSurface? _frameSurface;

    public Action<string>? StatusReporter { get; set; }
    public event Action? Closed;

    /// <summary>应用内最大化：由面板把当前源所在的墙格铺满整墙。</summary>
    public event Action? RequestInAppMaximize;

    public VideoWallPlayerControl()
    {
        InitializeComponent();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => RefreshPlaybackProgress();
        _chromeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _chromeHideTimer.Tick += (_, _) => HidePlaybackChrome();
        _volumePopupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            if (!_volumeDragging && !_volumePopupPinned)
                PlaybackVolumePopup.IsOpen = false;
        };
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
        ApplyLoc();
        SyncSpeedButton();
    }

    private Window? _hostWindow;

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        InheritHostBrushes(Window.GetWindow(this) ?? (FrameworkElement?)Parent);
        _frameSurface ??= new VideoWallFrameSurface(PlaybackFrameImage);
        // 代码绑定 PlacementTarget，避免 ElementName 在 Collapsed chrome / 独立 Popup 树下 Data Error 4
        PlaybackQualityPopup.PlacementTarget = PlaybackQualityBtn;
        PlaybackEpisodesPopup.PlacementTarget = PlaybackEpisodesBtn;
        PlaybackSettingsPopup.PlacementTarget = PlaybackSettingsBtn;
        PlaybackVolumePopup.PlacementTarget = PlaybackVolumeHost;
        HookHostWindow(Window.GetWindow(this));
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookHostWindow();
        SuspendPlaybackUi();
    }

    private void HookHostWindow(Window? window)
    {
        if (ReferenceEquals(_hostWindow, window)) return;
        UnhookHostWindow();
        _hostWindow = window;
        if (window is null) return;
        window.IsVisibleChanged += OnHostIsVisibleChanged;
        window.Closing += OnHostClosing;
    }

    private void UnhookHostWindow()
    {
        if (_hostWindow is null) return;
        _hostWindow.IsVisibleChanged -= OnHostIsVisibleChanged;
        _hostWindow.Closing -= OnHostClosing;
        _hostWindow = null;
    }

    private void OnHostIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hostWindow?.IsVisible == true)
        {
            if (IsVisible && (_chromeTitleVisible || _chromeTransportVisible))
                ShowPlaybackChrome();
            return;
        }

        SuspendPlaybackUi();
    }

    private void OnHostClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        => SuspendPlaybackUi();

    /// <summary>主窗隐藏/关闭：收起画中画/全屏与菜单（软件帧无独立浮层）。</summary>
    public void SuspendPlaybackUi()
    {
        try
        {
            CloseMenuPopups();
            ExitPip(restoreUi: false);
            ExitFullscreen(restoreUi: false);
            ForceHidePlaybackChrome();
        }
        catch { /* 关闭过程中可视树可能已半拆 */ }
    }

    /// <summary>兼容面板旧调用名。</summary>
    public void SuspendNativeOverlays() => SuspendPlaybackUi();

    /// <summary>兼容面板旧调用名。</summary>
    public void RestoreNativeOverlays()
    {
        if (!IsVisible) return;
        if (_chromeTitleVisible || _chromeTransportVisible)
            ShowPlaybackChrome();
    }

    public void AttachService(IVideoWallService? service)
    {
        if (ReferenceEquals(_service, service)) return;
        DetachServiceSubscriptions();
        _service = service;
        if (_service is null) return;
        _service.SinglePlayback.StatsChanged += OnStatsChanged;
        _service.SinglePlayback.StateChanged += OnPlaybackState;
    }

    public async Task PlayAsync(string sourceId, string title)
    {
        _pendingPlayId = sourceId;
        PlaybackTitle.Text = title;
        _cueLengthMs = -1;
        RefreshCueLayer();
        UpdateQualityUi(null);
        _frameSurface ??= new VideoWallFrameSurface(PlaybackFrameImage);
        ShowPlaybackChrome();
        BumpPlaybackChrome();
        ReportLog($"视频墙连接中: {title}");
        await StartPlaybackAsync(sourceId);
    }

    public async Task StopAsync()
    {
        _chromeHideTimer.Stop();
        _progressTimer.Stop();
        CloseMenuPopups();
        ExitPip(restoreUi: false);
        ExitFullscreen(restoreUi: false);
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _frameSurface?.Clear();
        _pendingPlayId = null;
        PlaybackTitle.Text = "";
        ResetSeekUi();
        ForceHidePlaybackChrome();
    }

    private void ForceHidePlaybackChrome()
    {
        _chromeHideTimer.Stop();
        _chromeTitleVisible = false;
        _chromeTransportVisible = false;
        try { PlaybackVolumePopup.IsOpen = false; } catch { /* 可能已脱离树 */ }
        try
        {
            PlaybackTitleBarHost.Visibility = Visibility.Collapsed;
            PlaybackTransportSlot.Visibility = Visibility.Collapsed;
            PlaybackTransportBar.Visibility = Visibility.Collapsed;
        }
        catch { /* ignore */ }
    }

    private void DetachServiceSubscriptions()
    {
        if (_service is null) return;
        _service.SinglePlayback.StatsChanged -= OnStatsChanged;
        _service.SinglePlayback.StateChanged -= OnPlaybackState;
    }

    private void ApplyLoc()
    {
        PlaybackBackBtn.ToolTip = Loc.T("Tool.VideoWall.Back");
        PlaybackSeekBackBtn.ToolTip = Loc.T("Tool.VideoWall.SeekBack");
        PlaybackSeekFwdBtn.ToolTip = Loc.T("Tool.VideoWall.SeekForward5");
        PlaybackStopBtn.ToolTip = Loc.T("Tool.VideoWall.Stop");
        PlaybackPlayPauseBtn.ToolTip = Loc.T("Tool.VideoWall.Pause");
        PlaybackMuteBtn.ToolTip = Loc.T("Tool.VideoWall.Mute");
        PlaybackVolumeBar.ToolTip = Loc.T("Tool.VideoWall.Volume");
        PlaybackSpeedBtn.ToolTip = Loc.T("Tool.VideoWall.Speed");
        PlaybackFullscreenBtn.ToolTip = Loc.T("Tool.VideoWall.Fullscreen");
        PlaybackQualityBtn.ToolTip = Loc.T("Tool.VideoWall.Quality");
        PlaybackEpisodesBtn.Content = Loc.T("Tool.VideoWall.Episodes");
        PlaybackEpisodesBtn.ToolTip = Loc.T("Tool.VideoWall.Episodes");
        PlaybackSettingsBtn.ToolTip = Loc.T("Tool.VideoWall.Settings");
        PlaybackPipBtn.ToolTip = Loc.T("Tool.VideoWall.PictureInPicture");
        PlaybackMaximizeBtn.ToolTip = Loc.T("Tool.VideoWall.Maximize");
        PlaybackQualityTitle.Text = Loc.T("Tool.VideoWall.Quality");
        PlaybackQualityAutoText.Text = $"{Loc.T("Tool.VideoWall.QualityAuto")}  ✓";
        PlaybackEpisodesTitle.Text = Loc.T("Tool.VideoWall.Episodes");
        PlaybackSettingsTitle.Text = Loc.T("Tool.VideoWall.Settings");
        PlaybackLoopLabel.Text = Loc.T("Tool.VideoWall.Loop");
        PlaybackEpisodeTitleLabel.Text = Loc.T("Tool.VideoWall.EpisodeTitle");
        PlaybackAspectLabel.Text = Loc.T("Tool.VideoWall.Aspect");
        PlaybackAspectValue.Text = Loc.T("Tool.VideoWall.AspectFit");
        PlaybackDiagLabel.Text = Loc.T("Tool.VideoWall.Diagnostics");
        SyncSpeedButton();
        UpdateQualityUi(_service?.SinglePlayback.LastStats);
    }

    private void InheritHostBrushes(FrameworkElement? host)
    {
        if (host is null) return;
        foreach (var key in new[] { "Blue", "BlueHover", "ControlBg", "ControlBorder", "Text" })
        {
            if (host.TryFindResource(key) is SolidColorBrush brush)
                Resources[key] = brush.Clone();
        }
    }

    private void OnStatsChanged(MediaStats stats) =>
        Dispatcher.BeginInvoke(() =>
        {
            UpdateQualityUi(stats);
            if (stats.Width > 0)
                ReportLog(
                    $"视频墙播放中 {PlaybackTitle.Text}: {stats.Width}×{stats.Height} @ {stats.Fps:F1} fps");
        });

    private void OnPlaybackState(TilePlayState state) =>
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTransportChrome(state);
            if (state is TilePlayState.Playing or TilePlayState.Paused or TilePlayState.Buffering)
                _progressTimer.Start();
            else if (state is TilePlayState.Stopped or TilePlayState.Failed or TilePlayState.Idle)
                _progressTimer.Stop();

            if (state is TilePlayState.Failed or TilePlayState.Stalled)
            {
                var code = _service?.SinglePlayback.ErrorCode ?? VideoErrorCode.None;
                var title = string.IsNullOrWhiteSpace(PlaybackTitle.Text) ? "" : $"「{PlaybackTitle.Text}」";
                ReportLog(code == VideoErrorCode.None
                    ? $"视频墙播放失败{title}: {StatusTextFor(state)}"
                    : $"视频墙播放失败{title}: {StatusTextFor(state)} ({code})");
            }
        });

    private void ReportLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusReporter?.Invoke(message);
    }

    private static string StatusTextFor(TilePlayState state) => state switch
    {
        TilePlayState.Connecting => Loc.T("Tool.VideoWall.StatusConnecting"),
        TilePlayState.Buffering => Loc.T("Tool.VideoWall.StatusBuffering"),
        TilePlayState.Stalled => Loc.T("Tool.VideoWall.StatusStalled"),
        TilePlayState.Reconnecting => Loc.T("Tool.VideoWall.StatusReconnecting"),
        TilePlayState.Paused => Loc.T("Tool.VideoWall.Pause"),
        TilePlayState.Stopped => Loc.T("Tool.VideoWall.StatusStopped"),
        _ => state.ToString()
    };

    private async Task StartPlaybackAsync(string sourceId)
    {
        if (_service is null) return;
        try
        {
            _frameSurface ??= new VideoWallFrameSurface(PlaybackFrameImage);
            _frameSurface.Clear();
            await _service.SinglePlayback.PlaySourceAsync(
                sourceId, renderTarget: null, frameSink: _frameSurface);
            UpdateTransportChrome(_service.SinglePlayback.State);
            _progressTimer.Start();
            ReportLog($"视频墙播放: {PlaybackTitle.Text}");
        }
        catch (Exception ex)
        {
            _frameSurface?.Clear();
            _progressTimer.Stop();
            ReportLog($"视频墙播放异常「{PlaybackTitle.Text}」: {ex.Message}");
        }
    }

    private async void PlaybackBackBtn_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync();
        Closed?.Invoke();
    }

    private void PlaybackPlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
        BumpPlaybackChrome();
    }

    private void TogglePlayPause()
    {
        if (_service is null) return;
        var state = _service.SinglePlayback.State;
        if (state == TilePlayState.Paused)
        {
            _service.SinglePlayback.Resume();
            UpdateTransportChrome(_service.SinglePlayback.State);
        }
        else if (state is TilePlayState.Stopped or TilePlayState.Idle or TilePlayState.Failed)
        {
            var id = _pendingPlayId ?? _service.SinglePlayback.CurrentSourceId;
            if (!string.IsNullOrWhiteSpace(id))
                _ = StartPlaybackAsync(id);
        }
        else
        {
            _service.SinglePlayback.Pause();
            UpdateTransportChrome(_service.SinglePlayback.State);
        }
    }

    private async void PlaybackStop_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _progressTimer.Stop();
        await _service.SinglePlayback.StopAsync();
        _frameSurface?.Clear();
        ResetSeekUi();
        UpdateTransportChrome(TilePlayState.Stopped);
        BumpPlaybackChrome();
    }

    private void PlaybackSeekBack_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.SeekBy(-SeekStepMs);
        HoldSeekUi(_service.SinglePlayback.TimeMs);
        RefreshPlaybackProgress();
        BumpPlaybackChrome();
    }

    private void PlaybackSeekFwd_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.SeekBy(SeekStepMs);
        HoldSeekUi(_service.SinglePlayback.TimeMs);
        RefreshPlaybackProgress();
        BumpPlaybackChrome();
    }

    private void PlaybackMute_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.ToggleMute();
        SyncVolumeUiFromService();
        ShowVolumePopup();
        BumpPlaybackChrome();
    }

    private void PlaybackSpeedBtn_Click(object sender, RoutedEventArgs e)
    {
        _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
        var rate = SpeedSteps[_speedIndex];
        _service?.SinglePlayback.SetRate(rate);
        SyncSpeedButton();
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
        // StaysOpen=False 时点按钮会先关再触发 Click；同一弹层刚关就忽略这次 Click，避免立刻重开
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

    private void PlaybackQualityBtn_Click(object sender, RoutedEventArgs e)
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
            PlaybackQualityCurrentText.Visibility = Visibility.Visible;
        }
        else
        {
            PlaybackQualityBtn.Content = Loc.T("Tool.VideoWall.QualityAuto");
            PlaybackQualityCurrentText.Text = "";
            PlaybackQualityCurrentText.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaybackEpisodesBtn_Click(object sender, RoutedEventArgs e)
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
                var item = new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = s.Name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontSize = 13
                    },
                    Tag = s
                };
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

    private void PlaybackEpisodesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEpisodeSelection || _service is null) return;
        if (PlaybackEpisodesList.SelectedItem is not ListBoxItem { Tag: VideoSourceDescriptor src }) return;

        PlaybackEpisodesPopup.IsOpen = false;
        if (src.Id == (_service.SinglePlayback.CurrentSourceId ?? _pendingPlayId)) return;
        _ = PlayAsync(src.Id, src.Name);
    }

    private void PlaybackSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        PlaybackLoopSwitch.IsChecked = _service?.SinglePlayback.Loop == true;
        RefreshDiagnosticsText();
        ToggleMenuPopup(PlaybackSettingsPopup);
    }

    private void PlaybackLoopSwitch_Click(object sender, RoutedEventArgs e)
    {
        _service?.SinglePlayback.SetLoop(PlaybackLoopSwitch.IsChecked == true);
        BumpPlaybackChrome();
    }

    private void PlaybackEpisodeTitleSwitch_Click(object sender, RoutedEventArgs e)
    {
        PlaybackTitleDuration.Visibility = PlaybackEpisodeTitleSwitch.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        BumpPlaybackChrome();
    }

    private void PlaybackDiagRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RefreshDiagnosticsText();
        PlaybackDiagText.Visibility = PlaybackDiagText.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
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
        var state = _service!.SinglePlayback.State;
        PlaybackDiagText.Text =
            $"{stats.Width}×{stats.Height} @ {stats.Fps:F1} fps\n" +
            $"{decode} · {stats.BufferMs} ms · {StatusTextFor(state)}";
    }

    // ───────── 画中画 / 应用内最大化 ─────────

    private void PlaybackMaximize_Click(object sender, RoutedEventArgs e)
    {
        CloseMenuPopups();
        if (_fullscreen) ExitFullscreen();
        if (_pip) ExitPip();
        RequestInAppMaximize?.Invoke();
    }

    private void PlaybackPip_Click(object sender, RoutedEventArgs e)
    {
        if (_pip) ExitPip();
        else EnterPip();
        BumpPlaybackChrome();
    }

    private void EnterPip()
    {
        if (_pip || _pipWindow != null) return;
        if (_fullscreen) ExitFullscreen();
        CloseMenuPopups();

        const double width = 340, height = 220;
        var area = SystemParameters.WorkArea;
        var win = new Window
        {
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            Left = area.Right - width - 16,
            Top = area.Top + 16,
            Title = $"{(string.IsNullOrWhiteSpace(PlaybackTitle.Text) ? Loc.T("Tool.VideoWall.Name") : PlaybackTitle.Text)} · {Loc.T("Tool.VideoWall.PictureInPicture")}"
        };

        var root = new Grid();
        PlaybackVideoSlot.Child = null;
        PlaybackVideoHost.Margin = new Thickness(0);
        root.Children.Add(PlaybackVideoHost);
        win.Content = root;
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            ExitPip();
            e.Handled = true;
        };
        win.Closed += (_, _) =>
        {
            if (_pipWindow == win)
                ExitPip();
        };

        _pipWindow = win;
        _pip = true;
        UpdatePipChrome();
        win.Show();
        ShowPlaybackChrome();
    }

    private void ExitPip(bool restoreUi = true)
    {
        var win = _pipWindow;
        _pipWindow = null;
        _pip = false;

        if (win != null)
        {
            if (win.Content is Grid g)
                g.Children.Clear();
            try { win.Close(); } catch { /* ignore */ }
            PlaybackVideoSlot.Child = PlaybackVideoHost;
        }

        if (restoreUi && win != null)
            ShowPlaybackChrome();

        UpdatePipChrome();
    }

    private void UpdatePipChrome()
    {
        PlaybackPipBtn.ToolTip = Loc.T(
            _pip ? "Tool.VideoWall.PictureInPictureExit" : "Tool.VideoWall.PictureInPicture");
    }

    private void PlaybackVolumeHost_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupPinned = true;
        ShowVolumePopup();
        BumpPlaybackChrome();
    }

    private void PlaybackVolumeHost_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupPinned = false;
        ScheduleHideVolumePopup();
    }

    private void PlaybackVolumePopup_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupPinned = true;
        _volumePopupHideTimer.Stop();
        BumpPlaybackChrome();
    }

    private void PlaybackVolumePopup_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupPinned = false;
        ScheduleHideVolumePopup();
    }

    private void ShowVolumePopup()
    {
        _volumePopupHideTimer.Stop();
        PlaybackVolumePopup.IsOpen = true;
        SyncVolumeUiFromService();
    }

    private void ScheduleHideVolumePopup()
    {
        if (_volumeDragging) return;
        _volumePopupHideTimer.Stop();
        _volumePopupHideTimer.Start();
    }

    private void PlaybackVolumeBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _volumeDragging = true;
        ShowVolumePopup();
    }

    private void PlaybackVolumeBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        ApplyVolumeFromBar();
        _volumeDragging = false;
        ScheduleHideVolumePopup();
        BumpPlaybackChrome();
    }

    private void PlaybackVolumeBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PlaybackVolumePct.Text = $"{(int)Math.Round(PlaybackVolumeBar.Value)}%";
        if (!_volumeDragging) return;
        ApplyVolumeFromBar();
        BumpPlaybackChrome();
    }

    private void ApplyVolumeFromBar()
    {
        if (_service is null) return;
        _service.SinglePlayback.SetVolume((int)Math.Round(PlaybackVolumeBar.Value));
        PlaybackVolumePct.Text = $"{(int)Math.Round(PlaybackVolumeBar.Value)}%";
        UpdateMuteChrome();
    }

    private void SyncSpeedButton()
    {
        var rate = SpeedSteps[Math.Clamp(_speedIndex, 0, SpeedSteps.Length - 1)];
        PlaybackSpeedBtn.Content = Math.Abs(rate - 1f) < 0.01f
            ? Loc.T("Tool.VideoWall.Speed")
            : $"{rate:0.##}x";
    }

    private void PlaybackFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void PlaybackSeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _seekDragging = true;
        _seekPointerX = e.GetPosition(PlaybackSeekBar).X;
        // 按下即按坐标落点并 Seek；Handled 阻止 Slider/Thumb 默认链路
        ApplySeekFromPointerX(_seekPointerX.Value);
        PlaybackSeekBar.CaptureMouse();
        e.Handled = true;
    }

    private void PlaybackSeekBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_seekDragging || e.LeftButton != MouseButtonState.Pressed) return;
        _seekPointerX = e.GetPosition(PlaybackSeekBar).X;
        // 拖动中只预览，不连发 Seek（松手再提交最终目标）
        PreviewSeekFromPointerX(_seekPointerX.Value);
    }

    private void PlaybackSeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        EndSeekDrag(e.GetPosition(PlaybackSeekBar).X);
        e.Handled = true;
    }

    private void PlaybackSeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_seekDragging)
            EndSeekDrag(_seekPointerX);
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
        if (PlaybackSeekBar.IsMouseCaptured)
            PlaybackSeekBar.ReleaseMouseCapture();
        BumpPlaybackChrome();
    }

    private void PlaybackSeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_seekDragging || _service is null) return;
        var len = ResolveSeekLengthMs();
        if (len <= 0) return;
        var ms = (long)(e.NewValue / SeekBarMax * len);
        PlaybackTimeText.Text = FormatClock(ms);
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
        var ms = (long)(ratio * len);
        PlaybackSeekBar.Value = ratio * SeekBarMax;
        HoldSeekUi(ms);
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
        var w = Math.Max(1, PlaybackSeekBar.ActualWidth);
        return Math.Clamp((x - SeekThumbWidth / 2) / Math.Max(1, w - SeekThumbWidth), 0, 1);
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
            var near = Math.Abs(time - _seekHoldMs) <= SeekCatchupMs;
            if (near)
            {
                if (_seekHoldNearSinceTick < 0)
                    _seekHoldNearSinceTick = now;
                else if (now - _seekHoldNearSinceTick >= SeekCatchupHoldMs)
                    _seekHoldMs = -1;
            }
            else
            {
                _seekHoldNearSinceTick = -1;
                // 超时仍远离：继续显示目标，避免同 tick 立刻弹回；下次用户操作会覆盖
                time = _seekHoldMs;
                if (now >= _seekHoldUntilTick)
                {
                    /* 保持 hold 显示，不强制清；仅当再次贴近才解除 */
                }
            }

            if (_seekHoldMs >= 0)
                time = _seekHoldMs;
        }

        PlaybackTimeText.Text = FormatClock(time);
        PlaybackLengthText.Text = len > 0 ? FormatClock(len) : "--:--";
        PlaybackTitleDuration.Text = len > 0 ? FormatClock(len) : "";
        // 以时长为主，不把瞬时 CanSeek 当成禁用条件
        PlaybackSeekBar.IsEnabled = len > 0;
        if (len > 0)
            PlaybackSeekBar.Value = Math.Clamp(time * SeekBarMax / len, 0, SeekBarMax);
        if (len != _cueLengthMs)
            RefreshCueLayer();
        if (!_volumeDragging)
            SyncVolumeUiFromService();
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

    private void PlaybackSeekBar_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshCueLayer();

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

            var hit = new Grid
            {
                Width = CueHitSize,
                Height = CueHitSize,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = cue,
                ToolTip = string.IsNullOrWhiteSpace(cue.Label)
                    ? FormatClock(cue.PositionMs)
                    : $"{FormatClock(cue.PositionMs)}  {cue.Label}"
            };
            hit.Children.Add(new Ellipse
            {
                Width = CueDotSize,
                Height = CueDotSize,
                Fill = Brushes.White,
                IsHitTestVisible = false
            });
            hit.MouseLeftButtonDown += Cue_MouseLeftButtonDown;
            hit.MouseRightButtonUp += Cue_MouseRightButtonUp;

            Canvas.SetLeft(hit, CueXFor(cue.PositionMs, len) - CueHitSize / 2);
            Canvas.SetTop(hit, (PlaybackCueLayer.Height - CueHitSize) / 2);
            PlaybackCueLayer.Children.Add(hit);
        }
    }

    private double CueXFor(long ms, long len)
    {
        var w = PlaybackSeekBar.ActualWidth;
        var ratio = Math.Clamp((double)ms / len, 0, 1);
        return SeekThumbWidth / 2 + ratio * Math.Max(0, w - SeekThumbWidth);
    }

    private void Cue_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_service is null || sender is not FrameworkElement { Tag: PlaybackCue cue }) return;
        if (!_service.SinglePlayback.CanSeek) return;
        _service.SinglePlayback.SeekTo(cue.PositionMs);
        HoldSeekUi(cue.PositionMs);
        RefreshPlaybackProgress();
        BumpPlaybackChrome();
    }

    private void Cue_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: PlaybackCue cue } fe) return;

        var menu = new ContextMenu { PlacementTarget = fe, Placement = PlacementMode.Top };
        var remove = new MenuItem { Header = Loc.T("Tool.VideoWall.RemoveCue") };
        remove.Click += async (_, _) => await RemoveCueAsync(cue);
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private void PlaybackSeekBar_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0) return;

        var w = PlaybackSeekBar.ActualWidth;
        var x = e.GetPosition(PlaybackSeekBar).X;
        var ratio = Math.Clamp((x - SeekThumbWidth / 2) / Math.Max(1, w - SeekThumbWidth), 0, 1);
        _ = AddCueAsync((long)(ratio * len));
    }

    private async Task AddCueAsync(long positionMs)
    {
        if (_service is null) return;
        var current = _service.SinglePlayback.GetKeyEvents();
        if (string.IsNullOrWhiteSpace(_service.SinglePlayback.CurrentSourceId))
        {
            ReportLog(Loc.T("Tool.VideoWall.CueNoSource"));
            return;
        }

        // 同一位置 1 秒内视为重复
        if (current.Any(c => Math.Abs(c.PositionMs - positionMs) < 1000)) return;

        var next = current.Append(new PlaybackCue(positionMs));
        if (!await _service.SinglePlayback.SetKeyEventsAsync(next)) return;
        await Dispatcher.InvokeAsync(() =>
        {
            RefreshCueLayer();
            ReportLog($"{Loc.T("Tool.VideoWall.AddCue")}: {FormatClock(positionMs)}");
        });
    }

    private async Task RemoveCueAsync(PlaybackCue cue)
    {
        if (_service is null) return;
        var next = _service.SinglePlayback.GetKeyEvents().Where(c => c != cue);
        if (!await _service.SinglePlayback.SetKeyEventsAsync(next)) return;
        await Dispatcher.InvokeAsync(RefreshCueLayer);
    }

    private void UpdateTransportChrome(TilePlayState state)
    {
        var pausedOrStopped = state is TilePlayState.Paused or TilePlayState.Stopped
            or TilePlayState.Idle or TilePlayState.Failed;
        PlaybackPlayPauseIcon.Data = pausedOrStopped
            ? (Geometry)FindResource("GeoPlay")
            : (Geometry)FindResource("GeoPause");
        PlaybackPlayPauseBtn.ToolTip = pausedOrStopped
            ? Loc.T(state == TilePlayState.Paused ? "Tool.VideoWall.Resume" : "Tool.VideoWall.Play")
            : Loc.T("Tool.VideoWall.Pause");
        UpdateMuteChrome();
        SyncVolumeUiFromService();
        RefreshPlaybackProgress();
        UpdateFullscreenChrome();
    }

    private void SyncVolumeUiFromService()
    {
        if (_service is null || _volumeDragging) return;
        var vol = _service.SinglePlayback.Volume;
        if (Math.Abs(PlaybackVolumeBar.Value - vol) > 0.5)
            PlaybackVolumeBar.Value = vol;
        PlaybackVolumePct.Text = $"{vol}%";
        UpdateMuteChrome();
    }

    private void UpdateMuteChrome()
    {
        var muted = _service?.SinglePlayback.IsMuted == true;
        PlaybackMuteIcon.Data = muted
            ? (Geometry)FindResource("GeoMute")
            : (Geometry)FindResource("GeoVolume");
        PlaybackMuteBtn.ToolTip = Loc.T(muted ? "Tool.VideoWall.Unmute" : "Tool.VideoWall.Mute");
    }

    private void UpdateFullscreenChrome()
    {
        PlaybackFullscreenIcon.Data = _fullscreen
            ? (Geometry)FindResource("GeoFullscreenExit")
            : (Geometry)FindResource("GeoFullscreen");
        PlaybackFullscreenBtn.ToolTip = Loc.T(
            _fullscreen ? "Tool.VideoWall.FullscreenExit" : "Tool.VideoWall.Fullscreen");
    }

    private void EnterFullscreen()
    {
        if (_fullscreen || _fullscreenWindow != null) return;
        if (_pip) ExitPip();
        CloseMenuPopups();

        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowState = WindowState.Maximized,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            Title = string.IsNullOrWhiteSpace(PlaybackTitle.Text)
                ? Loc.T("Tool.VideoWall.Name")
                : PlaybackTitle.Text
        };

        // 软件帧：画面与 chrome 同在 PlaybackVideoHost，整棵迁入全屏窗即可
        var root = new Grid();
        PlaybackVideoSlot.Child = null;
        root.Children.Add(PlaybackVideoHost);
        win.Content = root;

        win.PreviewKeyDown += FullscreenWindow_PreviewKeyDown;
        win.MouseMove += FullscreenWindow_MouseMove;
        win.Closed += (_, _) =>
        {
            if (_fullscreenWindow == win)
                ExitFullscreen();
        };

        _fullscreenWindow = win;
        _fullscreen = true;
        UpdateFullscreenChrome();
        win.Show();
        win.Activate();
        BumpPlaybackChrome();
    }

    private void ExitFullscreen(bool restoreUi = true)
    {
        _chromeHideTimer.Stop();
        var win = _fullscreenWindow;
        _fullscreenWindow = null;
        _fullscreen = false;

        if (win != null)
        {
            win.PreviewKeyDown -= FullscreenWindow_PreviewKeyDown;
            win.MouseMove -= FullscreenWindow_MouseMove;
            if (win.Content is Grid g)
                g.Children.Clear();
            try { win.Close(); } catch { /* ignore */ }

            PlaybackVideoSlot.Child = PlaybackVideoHost;
        }

        if (restoreUi)
            ShowPlaybackChrome();

        UpdateFullscreenChrome();
    }

    private void FullscreenWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitFullscreen();
            e.Handled = true;
        }
        else
            BumpPlaybackChrome();
    }

    private void FullscreenWindow_MouseMove(object sender, MouseEventArgs e) => BumpPlaybackChrome();

    private void Root_MouseMove(object sender, MouseEventArgs e) => BumpPlaybackChrome();

    private void PlaybackChromeOverlay_MouseMove(object sender, MouseEventArgs e) => BumpPlaybackChrome();

    private void PlaybackChromeOverlay_MouseEnter(object sender, MouseEventArgs e) => BumpPlaybackChrome();

    private void PlaybackChromeOverlay_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_seekDragging || _volumeDragging || AnyMenuPopupOpen()) return;
        if (IsChromeInteractive(e.OriginalSource as DependencyObject)) return;
        TogglePlayPause();
        BumpPlaybackChrome();
        e.Handled = true;
    }

    private static bool IsChromeInteractive(DependencyObject? src)
    {
        while (src != null)
        {
            if (src is ButtonBase or Slider or ListBox or Thumb or TextBoxBase)
                return true;
            src = VisualTreeHelper.GetParent(src);
        }

        return false;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_chromeTitleVisible && !_chromeTransportVisible) return;
        ShowPlaybackChrome();
    }

    private void BumpPlaybackChrome()
    {
        if (Visibility != Visibility.Visible) return;
        ShowPlaybackChrome();
        if (_fullscreen && _fullscreenWindow != null)
            _fullscreenWindow.Cursor = Cursors.Arrow;
        else
            Cursor = Cursors.Arrow;

        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    private void ShowPlaybackChrome()
    {
        var showTransport = _fullscreen || !IsCompactChrome();
        _chromeTitleVisible = !_fullscreen;
        _chromeTransportVisible = showTransport;

        if (_fullscreen)
        {
            PlaybackTitleBarHost.Visibility = Visibility.Collapsed;
            PlaybackTransportBar.Visibility = showTransport ? Visibility.Visible : Visibility.Collapsed;
            ApplyVideoChromeInset(title: false, transport: showTransport);
            return;
        }

        PlaybackTitleBarHost.Visibility = Visibility.Visible;
        PlaybackTransportSlot.Visibility = showTransport ? Visibility.Visible : Visibility.Collapsed;
        PlaybackTransportBar.Visibility = showTransport ? Visibility.Visible : Visibility.Collapsed;
        ApplyVideoChromeInset(title: true, transport: showTransport);
    }

    private void HidePlaybackChrome()
    {
        _chromeHideTimer.Stop();
        if (Visibility != Visibility.Visible) return;
        if (_seekDragging || _volumeDragging || AnyMenuPopupOpen()) return;

        _chromeTitleVisible = false;
        _chromeTransportVisible = false;
        PlaybackVolumePopup.IsOpen = false;

        if (_fullscreen && _fullscreenWindow != null)
        {
            PlaybackTransportBar.Visibility = Visibility.Collapsed;
            ApplyVideoChromeInset(title: false, transport: false);
            _fullscreenWindow.Cursor = Cursors.None;
            return;
        }

        PlaybackTitleBarHost.Visibility = Visibility.Collapsed;
        PlaybackTransportSlot.Visibility = Visibility.Collapsed;
        PlaybackTransportBar.Visibility = Visibility.Collapsed;
        ApplyVideoChromeInset(title: false, transport: false);
    }

    private bool IsCompactChrome()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (_fullscreen && _fullscreenWindow != null)
        {
            w = _fullscreenWindow.ActualWidth;
            h = _fullscreenWindow.ActualHeight;
        }

        return w > 0 && h > 0 && (w < CompactChromeThreshold || h < CompactChromeThreshold);
    }

    private void ApplyVideoChromeInset(bool title, bool transport)
    {
        // Win：标题/进度条在 VideoView.Content（ForegroundWindow）上，视频全铺无需 inset。
        _ = title;
        _ = transport;
        PlaybackVideoHost.Margin = new Thickness(0);
    }

    private static LinearGradientBrush CreateTitleChromeBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(0xF2, 0x0B, 0x12, 0x20), 0),
            new GradientStop(Color.FromArgb(0xCC, 0x0B, 0x12, 0x20), 0.55),
            new GradientStop(Color.FromArgb(0x00, 0x0B, 0x12, 0x20), 1)
        ]
    };

    private static LinearGradientBrush CreateTransportChromeBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(0x00, 0x0B, 0x12, 0x20), 0),
            new GradientStop(Color.FromArgb(0x99, 0x0B, 0x12, 0x20), 0.35),
            new GradientStop(Color.FromArgb(0xE6, 0x0B, 0x12, 0x20), 0.75),
            new GradientStop(Color.FromArgb(0xF2, 0x0B, 0x12, 0x20), 1)
        ]
    };

    private static string FormatClock(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}