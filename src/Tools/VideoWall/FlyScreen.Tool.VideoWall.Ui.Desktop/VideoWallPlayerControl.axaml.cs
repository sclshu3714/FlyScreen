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

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public sealed partial class VideoWallPlayerControl : UserControl
{
    private const long SeekStepMs = 5_000;
    private const double CueHitSize = 14;
    private const double CueDotSize = 6;
    private const double SeekBarMax = 1000;
    private const double CompactChromeThreshold = 500;
    private const double TitleChromeHeight = 56;
    private const double TransportChromeHeight = 128;
    private static readonly float[] SpeedSteps = [0.75f, 1f, 1.25f, 1.5f, 2f];

    private readonly DispatcherTimer _chromeHideTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _volumePopupHideTimer;

    private IVideoWallService? _service;
    private string? _pendingPlayId;
    private bool _fullscreen;
    private Window? _fullscreenWindow;
    private bool _volumeApplying;
    private bool _seekDragging;
    private long _seekHoldMs = -1;
    private long _seekHoldUntilTick;
    private bool _volumePopupPinned;
    private int _speedIndex = 1;
    private Window? _pipWindow;
    private Popup? _lastClosedMenuPopup;
    private DateTime _lastMenuClosedUtc;
    private bool _suppressEpisodeSelection;
    private bool _suppressSwitch;
    private long _cueLengthMs = -1;

    public Action<string>? StatusReporter { get; set; }
    public event Action? Closed;

    /// <summary>应用内最大化：由面板把当前源所在的墙格铺满整墙。</summary>
    public event Action? RequestInAppMaximize;

    public VideoWallPlayerControl()
    {
        InitializeComponent();
        _chromeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _chromeHideTimer.Tick += (_, _) => HidePlaybackChrome();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => RefreshPlaybackProgress();
        _volumePopupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            if (!_volumePopupPinned)
                PlaybackVolumePopup.IsOpen = false;
        };
        PlaybackVolumeBar.PropertyChanged += PlaybackVolumeBar_PropertyChanged;
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
        ShowPlaybackChrome();
        BumpPlaybackChrome();
        StatusReporter?.Invoke(Loc.T("Tool.VideoWall.StatusConnecting"));
        if (_service is null) return;
        try
        {
            await _service.SinglePlayback.StopAsync();
            await _service.SinglePlayback.PlaySourceAsync(sourceId);
            UpdateTransportChrome(_service.SinglePlayback.State);
            _progressTimer.Start();
            StatusReporter?.Invoke(Loc.T("Tool.VideoWall.Play"));
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            StatusReporter?.Invoke(ex.Message);
        }
    }

    public async Task StopAsync()
    {
        _chromeHideTimer.Stop();
        _progressTimer.Stop();
        CloseMenuPopups();
        ExitPip();
        ExitFullscreen();
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        PlaybackTitle.Text = "";
        PlaybackTitleDuration.Text = "";
        ResetSeekUi();
        PlaybackTitleBarHost.IsVisible = false;
        PlaybackTransportBar.IsVisible = false;
        PlaybackVolumePopup.IsOpen = false;
        ApplyVideoChromeInset(title: false, transport: false);
    }

    private void DetachServiceSubscriptions()
    {
        if (_service is null) return;
        _service.SinglePlayback.StateChanged -= OnPlaybackStateChanged;
        _service.SinglePlayback.StatsChanged -= OnStatsChanged;
    }

    private void OnStatsChanged(MediaStats stats) =>
        Dispatcher.UIThread.Post(() => UpdateQualityUi(stats));

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
        VideoPlaceholder.Text = Loc.T("Tool.VideoWall.VideoSurfaceTodo");
        SyncSpeedButton();
    }

    private void OnPlaybackStateChanged(TilePlayState state) =>
        Dispatcher.UIThread.Post(() => UpdateTransportChrome(state));

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
        _service?.SinglePlayback.SeekBy(SeekStepMs);
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

    // ───────── 画中画 / 应用内最大化 ─────────

    private void PlaybackMaximize_Click(object? sender, RoutedEventArgs e)
    {
        CloseMenuPopups();
        if (_fullscreen) ExitFullscreen();
        ExitPip();
        RequestInAppMaximize?.Invoke();
    }

    private void PlaybackPip_Click(object? sender, RoutedEventArgs e)
    {
        if (_pipWindow is not null) ExitPip();
        else EnterPip();
        BumpPlaybackChrome();
    }

    /// <summary>
    /// Avalonia 侧尚无嵌入视频面：画中画为置顶小窗（标题 + 状态），播放仍由 SinglePlayback 持续。
    /// </summary>
    private void EnterPip()
    {
        if (_pipWindow is not null) return;
        if (_fullscreen) ExitFullscreen();
        CloseMenuPopups();

        var title = string.IsNullOrWhiteSpace(PlaybackTitle.Text)
            ? Loc.T("Tool.VideoWall.Name")
            : PlaybackTitle.Text!;
        var label = new TextBlock
        {
            Text = $"{title}\n{Loc.T("Tool.VideoWall.PictureInPicture")}",
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(12)
        };

        var win = new Window
        {
            Width = 340,
            Height = 200,
            Topmost = true,
            CanResize = true,
            ShowInTaskbar = false,
            Background = Brushes.Black,
            Title = $"{title} · {Loc.T("Tool.VideoWall.PictureInPicture")}",
            Content = new Border { Background = Brushes.Black, Child = label }
        };

        try
        {
            if (TopLevel.GetTopLevel(this)?.Screens?.Primary is { } screen)
            {
                var scale = screen.Scaling;
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Position = new PixelPoint(
                    screen.WorkingArea.Right - (int)((340 + 16) * scale),
                    screen.WorkingArea.Y + (int)(16 * scale));
            }
        }
        catch { /* 取不到屏幕时用系统默认位置 */ }

        win.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            ExitPip();
            e.Handled = true;
        };
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pipWindow, win))
            {
                _pipWindow = null;
                UpdatePipChrome();
            }
        };

        _pipWindow = win;
        win.Show();
        UpdatePipChrome();
    }

    private void ExitPip()
    {
        var win = _pipWindow;
        _pipWindow = null;
        if (win is not null)
        {
            try { win.Close(); } catch { /* ignore */ }
        }

        UpdatePipChrome();
    }

    private void UpdatePipChrome()
        => ToolTip.SetTip(PlaybackPipBtn, Loc.T(
            _pipWindow is not null ? "Tool.VideoWall.PictureInPictureExit" : "Tool.VideoWall.PictureInPicture"));

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
        PlaybackVolumePopup.IsOpen = true;
        SyncVolumeUi();
    }

    private void ScheduleHideVolumePopup()
    {
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

    private void PlaybackFullscreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
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

    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host is null) return;

        _fullscreenWindow = host;
        _fullscreen = true;
        host.WindowState = WindowState.FullScreen;
        PlaybackFullscreenIcon.Data = (Geometry?)Resources["GeoFullscreenExit"];
        ToolTip.SetTip(PlaybackFullscreenBtn, Loc.T("Tool.VideoWall.FullscreenExit"));
        host.PointerMoved += Fullscreen_PointerMoved;
        host.KeyDown += Fullscreen_KeyDown;
        BumpPlaybackChrome();
    }

    private void ExitFullscreen()
    {
        _chromeHideTimer.Stop();
        var host = _fullscreenWindow;
        _fullscreenWindow = null;
        if (host is not null)
        {
            host.PointerMoved -= Fullscreen_PointerMoved;
            host.KeyDown -= Fullscreen_KeyDown;
            if (host.WindowState == WindowState.FullScreen)
                host.WindowState = WindowState.Normal;
        }

        _fullscreen = false;
        ShowPlaybackChrome();
        PlaybackFullscreenIcon.Data = (Geometry?)Resources["GeoFullscreen"];
        ToolTip.SetTip(PlaybackFullscreenBtn, Loc.T("Tool.VideoWall.Fullscreen"));
    }

    private void Fullscreen_PointerMoved(object? sender, PointerEventArgs e) => BumpPlaybackChrome();

    private void Fullscreen_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitFullscreen();
            e.Handled = true;
        }
        else BumpPlaybackChrome();
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
        if (!IsVisible) return;
        ShowPlaybackChrome();
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    private void ShowPlaybackChrome()
    {
        var showTransport = _fullscreen || !IsCompactChrome();
        PlaybackTitleBarHost.IsVisible = !_fullscreen;
        PlaybackTransportBar.IsVisible = showTransport;
        ApplyVideoChromeInset(title: !_fullscreen, transport: showTransport);
    }

    private void HidePlaybackChrome()
    {
        _chromeHideTimer.Stop();
        if (!IsVisible || _seekDragging || AnyMenuPopupOpen()) return;
        PlaybackTitleBarHost.IsVisible = false;
        PlaybackTransportBar.IsVisible = false;
        PlaybackVolumePopup.IsOpen = false;
        ApplyVideoChromeInset(title: false, transport: false);
    }

    private bool IsCompactChrome()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (_fullscreen && _fullscreenWindow != null)
        {
            w = _fullscreenWindow.Bounds.Width;
            h = _fullscreenWindow.Bounds.Height;
        }

        return w > 0 && h > 0 && (w < CompactChromeThreshold || h < CompactChromeThreshold);
    }

    private void ApplyVideoChromeInset(bool title, bool transport)
    {
        // Desktop 无 HWND 空气空间时可叠层；仍留边以与 Win/Maui 视觉一致
        var top = title ? TitleChromeHeight : 0;
        var bottom = transport ? TransportChromeHeight : 0;
        PlaybackVideoHost.Margin = new Thickness(0, top, 0, bottom);
    }

    private void PlaybackSeekBar_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty || !_seekDragging || _service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0) return;
        var ms = (long)(PlaybackSeekBar.Value / SeekBarMax * len);
        PlaybackTimeText.Text = FormatClock(ms);
    }

    private void PlaybackSeekBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PlaybackSeekBar).Properties.IsLeftButtonPressed)
            _seekDragging = true;
    }

    private void PlaybackSeekBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        ApplySeekFromBar();
        _seekDragging = false;
        BumpPlaybackChrome();
    }

    private void ApplySeekFromBar()
    {
        if (_service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0 || !_service.SinglePlayback.CanSeek) return;
        var ms = (long)(PlaybackSeekBar.Value / SeekBarMax * len);
        _service.SinglePlayback.SeekTo(ms);
        // Seek 后 TimeMs 短暂仍为旧值；立刻 Refresh 会把滑块弹回
        _seekHoldMs = ms;
        _seekHoldUntilTick = Environment.TickCount64 + 600;
        PlaybackTimeText.Text = FormatClock(ms);
    }

    private void RefreshPlaybackProgress()
    {
        if (_service is null || _seekDragging) return;
        var time = _service.SinglePlayback.TimeMs;
        var len = _service.SinglePlayback.LengthMs;
        if (_seekHoldMs >= 0)
        {
            if (Environment.TickCount64 < _seekHoldUntilTick && Math.Abs(time - _seekHoldMs) > 1000)
                time = _seekHoldMs;
            else
                _seekHoldMs = -1;
        }

        PlaybackTimeText.Text = FormatClock(time);
        PlaybackLengthText.Text = len > 0 ? FormatClock(len) : "--:--";
        PlaybackTitleDuration.Text = len > 0 ? FormatClock(len) : "";
        PlaybackSeekBar.IsEnabled = _service.SinglePlayback.CanSeek && len > 0;
        if (len > 0)
            PlaybackSeekBar.Value = Math.Clamp(time * SeekBarMax / len, 0, SeekBarMax);
        if (len != _cueLengthMs)
            RefreshCueLayer();
    }

    private void ResetSeekUi()
    {
        _seekHoldMs = -1;
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
