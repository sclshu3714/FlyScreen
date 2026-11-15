using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Wall;
using Microsoft.Maui.Controls.Shapes;
using Grid = Microsoft.Maui.Controls.Grid;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

public sealed class VideoWallPlayerControl : ContentView
{
    private const long SeekStepMs = 5_000;
    private const double SeekBarMax = 1000;
    private const double SeekThumbWidth = 20;
    private const double CueHitHeight = 16;
    private const double CueDotSize = 6;
    private const double CompactChromeThreshold = 500;
    private const double TitleChromeHeight = 56;
    private const double TransportChromeHeight = 128;
    private static readonly float[] SpeedSteps = [0.75f, 1f, 1.25f, 1.5f, 2f];

    private static readonly Color PlaybackAccent = Color.FromArgb("#00D4C8");
    private static readonly Color PlaybackOnBarText = Color.FromArgb("#CCE5E7EB");
    private static readonly Color PlaybackTitleMuted = Color.FromArgb("#94A3B8");

    private readonly Grid _root;
    private readonly Label _playbackTitle;
    private readonly Border _playbackVideoHost;
#if WINDOWS
    private readonly VideoWallHwndSurface _playbackSurface;
#else
    private readonly Label _playbackVideoPlaceholder;
#endif
    private readonly Border _playbackPlayPauseBtn;
    private readonly Path _playbackPlayPauseIcon;
    private readonly Slider _playbackSeekBar;
    private readonly Slider _playbackVolumeBar;
    private readonly Label _playbackVolumePct;
    private readonly Label _playbackTimeText;
    private readonly Label _playbackLengthText;
    private readonly Border _playbackMuteBtn;
    private readonly Path _playbackMuteIcon;
    private readonly Border _playbackVolumePopup;
    private readonly Label _playbackSpeedBtn;
    private readonly Border _playbackTitleBarHost;
    private readonly Grid _playbackTitleBar;
    private readonly Border _playbackTransportBar;
    private readonly Label _playbackTitleDuration;
    private readonly AbsoluteLayout _cueLayer;
    private readonly Label _qualityBtn;
    private readonly Label _episodesBtn;
    private readonly Border _menuHost;
    private readonly Border _pipExitBtn;
    private readonly Path _fullscreenIcon;

    private IVideoWallService? _service;
    private string? _pendingPlayId;
    private bool _seekDragging;
    private bool _volumeDragging;
    private int _speedIndex = 1;
    private long _cueLengthMs = -1;
    private bool _showEpisodeTitle = true;
    private bool _pip;
    private bool _fullscreen;
    private IDispatcherTimer? _progressTimer;
    private IDispatcherTimer? _chromeHideTimer;
    private IDispatcherTimer? _volumePopupHideTimer;
#if WINDOWS
    private VideoWallMauiChromeOverlay? _chromeOverlay;
#endif

    public Action<string>? StatusReporter { get; set; }
    public event Action? Closed;

    /// <summary>应用内最大化：由面板把当前源所在的墙格铺满整墙。</summary>
    public event Action? RequestInAppMaximize;

    public VideoWallPlayerControl()
    {
        _playbackTitle = new Label
        {
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 520
        };
        _playbackTitleDuration = new Label
        {
            FontSize = 13,
            TextColor = PlaybackTitleMuted,
            FontFamily = "Consolas",
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var playbackBackPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Back, Loc.T("Tool.VideoWall.Back"),
            margin: new Thickness(0), iconSize: 18);
        VideoWallMauiIcons.WireClicked(playbackBackPair.Host, async (_, _) => await PlaybackBackAsync());

#if WINDOWS
        _playbackSurface = new VideoWallHwndSurface
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        _playbackVideoHost = new Border
        {
            BackgroundColor = Colors.Black,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 0 },
            Margin = 0,
            Content = _playbackSurface
        };
#else
        _playbackVideoPlaceholder = new Label
        {
            Text = Loc.T("Tool.VideoWall.DesktopOnly"),
            TextColor = Colors.Gray,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.WordWrap,
            Margin = new Thickness(12)
        };
        _playbackVideoHost = new Border
        {
            BackgroundColor = Colors.Black,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 0 },
            Margin = 0,
            Content = _playbackVideoPlaceholder
        };
#endif

        var playPausePair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Pause, Loc.T("Tool.VideoWall.Pause"), iconSize: 22);
        _playbackPlayPauseBtn = playPausePair.Host;
        _playbackPlayPauseIcon = playPausePair.Icon;
        VideoWallMauiIcons.WireClicked(_playbackPlayPauseBtn, (_, _) => PlaybackPlayPause());

        var nextPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Next, Loc.T("Tool.VideoWall.SeekForward5"), iconSize: 20);
        VideoWallMauiIcons.WireClicked(nextPair.Host, (_, _) =>
        {
            _service?.SinglePlayback.SeekBy(SeekStepMs);
            RefreshPlaybackProgress();
            BumpPlaybackChrome();
        });

        var mutePair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Volume, Loc.T("Tool.VideoWall.Mute"),
            margin: new Thickness(0), iconSize: 18);
        _playbackMuteBtn = mutePair.Host;
        _playbackMuteIcon = mutePair.Icon;
        VideoWallMauiIcons.WireClicked(_playbackMuteBtn, (_, _) =>
        {
            _service?.SinglePlayback.ToggleMute();
            SyncVolumeUi();
            ShowVolumePopup();
            BumpPlaybackChrome();
        });

        var fullscreenPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Fullscreen, Loc.T("Tool.VideoWall.Fullscreen"),
            margin: new Thickness(0), iconSize: 16);
        _fullscreenIcon = fullscreenPair.Icon;
        VideoWallMauiIcons.WireClicked(fullscreenPair.Host, (_, _) =>
        {
            ToggleFullscreen();
            BumpPlaybackChrome();
        });

        var settingsPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Settings, Loc.T("Tool.VideoWall.Settings"), iconSize: 18);
        VideoWallMauiIcons.WireClicked(settingsPair.Host, (_, _) => ToggleMenu(MenuKind.Settings));

        var pipPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Pip, Loc.T("Tool.VideoWall.PictureInPicture"), iconSize: 18);
        VideoWallMauiIcons.WireClicked(pipPair.Host, (_, _) => TogglePictureInPicture());

        var maximizePair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Maximize, Loc.T("Tool.VideoWall.Maximize"), iconSize: 16);
        VideoWallMauiIcons.WireClicked(maximizePair.Host, (_, _) =>
        {
            CloseMenu();
            RestorePresenter();
            RequestInAppMaximize?.Invoke();
        });

        // 画中画（整窗缩小置顶）后控制条会收起，在标题栏右侧留退出入口
        var pipExitPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Pip, Loc.T("Tool.VideoWall.PictureInPictureExit"),
            margin: new Thickness(0), iconSize: 18);
        _pipExitBtn = pipExitPair.Host;
        _pipExitBtn.IsVisible = false;
        VideoWallMauiIcons.WireClicked(_pipExitBtn, (_, _) => TogglePictureInPicture());

        _playbackVolumePct = new Label
        {
            Text = "100%",
            FontSize = 11,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center
        };
        _playbackVolumeBar = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            WidthRequest = 120,
            HeightRequest = 28,
            // 底下垫整条 绿→青→蓝 渐变；已播段透明露出渐变，未播段用不透明深色盖住
            MinimumTrackColor = Colors.Transparent,
            MaximumTrackColor = Color.FromArgb("#FF3B4252"),
            ThumbColor = Colors.White
        };
        var volumeGradientTrack = new Border
        {
            HeightRequest = 4,
            WidthRequest = 112,
            StrokeThickness = 0,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
            StrokeShape = new RoundRectangle { CornerRadius = 2 },
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Colors.Lime, 0f),
                    new GradientStop(Colors.Cyan, 0.5f),
                    new GradientStop(Colors.SkyBlue, 1f)
                }
            }
        };
        // MAUI 无竖向 Slider：整体旋转 -90° 模拟附件竖向音量
        var volumeRotated = new Grid
        {
            WidthRequest = 120,
            HeightRequest = 28,
            Rotation = -90,
            Children = { volumeGradientTrack, _playbackVolumeBar }
        };
        _playbackVolumeBar.DragStarted += (_, _) =>
        {
            _volumeDragging = true;
            ShowVolumePopup();
        };
        _playbackVolumeBar.DragCompleted += (_, _) =>
        {
            ApplyVolumeFromBar();
            _volumeDragging = false;
            ScheduleHideVolumePopup();
            BumpPlaybackChrome();
        };
        _playbackVolumeBar.ValueChanged += (_, _) =>
        {
            _playbackVolumePct.Text = $"{(int)Math.Round(_playbackVolumeBar.Value)}%";
            if (_volumeDragging) ApplyVolumeFromBar();
        };

        _playbackVolumePopup = new Border
        {
            IsVisible = false,
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#33FFFFFF"),
            BackgroundColor = Color.FromArgb("#E6111827"),
            Padding = new Thickness(10, 12),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 150, 44),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                WidthRequest = 44,
                Children =
                {
                    _playbackVolumePct,
                    new Grid
                    {
                        HeightRequest = 120,
                        WidthRequest = 28,
                        HorizontalOptions = LayoutOptions.Center,
                        Children = { volumeRotated }
                    }
                }
            }
        };

        _playbackSpeedBtn = new Label
        {
            Text = Loc.T("Tool.VideoWall.Speed"),
            TextColor = Colors.White,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(10, 6)
        };
        var speedTap = new TapGestureRecognizer();
        speedTap.Tapped += (_, _) =>
        {
            _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
            var rate = SpeedSteps[_speedIndex];
            _service?.SinglePlayback.SetRate(rate);
            SyncSpeedButton();
            BumpPlaybackChrome();
        };
        _playbackSpeedBtn.GestureRecognizers.Add(speedTap);

        _qualityBtn = CreateChromeTextButton(Loc.T("Tool.VideoWall.QualityAuto"), () => ToggleMenu(MenuKind.Quality));
        _episodesBtn = CreateChromeTextButton(Loc.T("Tool.VideoWall.Episodes"), () => ToggleMenu(MenuKind.Episodes));

        _menuHost = new Border
        {
            IsVisible = false,
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#33FFFFFF"),
            BackgroundColor = Color.FromArgb("#F2161B26"),
            Padding = new Thickness(12, 10),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 16, 8),
            MinimumWidthRequest = 160,
            MaximumWidthRequest = 300,
            ZIndex = 5
        };
        // 点菜单本身不要冒泡成「点空白关闭」
        _menuHost.GestureRecognizers.Add(new TapGestureRecognizer());

        _playbackTimeText = new Label
        {
            Text = "00:00",
            FontSize = 11,
            TextColor = PlaybackOnBarText,
            FontFamily = "Consolas",
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.End
        };
        _playbackLengthText = new Label
        {
            Text = "00:00",
            FontSize = 11,
            TextColor = PlaybackOnBarText,
            FontFamily = "Consolas",
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End
        };

        _playbackSeekBar = new Slider
        {
            Minimum = 0,
            Maximum = SeekBarMax,
            Value = 0,
            MinimumTrackColor = PlaybackAccent,
            MaximumTrackColor = Color.FromArgb("#66FFFFFF"),
            ThumbColor = Colors.White,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _playbackSeekBar.DragStarted += (_, _) => _seekDragging = true;
        _playbackSeekBar.DragCompleted += (_, _) =>
        {
            ApplySeekFromBar();
            _seekDragging = false;
            BumpPlaybackChrome();
        };
        _playbackSeekBar.ValueChanged += (_, e) =>
        {
            if (!_seekDragging || _service is null) return;
            var len = _service.SinglePlayback.LengthMs;
            if (len <= 0) return;
            var ms = (long)(e.NewValue / SeekBarMax * len);
            _playbackTimeText.Text = FormatClock(ms);
        };

        // 关键事件白点层：与进度条同宽，位置按「滑块中心」比例排布
        _cueLayer = new AbsoluteLayout
        {
            HeightRequest = CueHitHeight,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var progressRow = new Grid
        {
            HeightRequest = 40,
            Children =
            {
                _playbackSeekBar,
                _cueLayer,
                _playbackTimeText,
                _playbackLengthText
            }
        };
        _playbackSeekBar.SizeChanged += (_, _) => RefreshCueLayer();

        // 桌面：右键进度条在该位置添加标记（触屏用设置菜单里的「添加标记」）
        var seekSecondaryTap = new TapGestureRecognizer { Buttons = ButtonsMask.Secondary };
        seekSecondaryTap.Tapped += (_, e) =>
        {
            var p = e.GetPosition(_playbackSeekBar);
            if (p is null || _service is null) return;
            var len = _service.SinglePlayback.LengthMs;
            if (len <= 0) return;
            var w = Math.Max(1, _playbackSeekBar.Width - SeekThumbWidth);
            var ratio = Math.Clamp((p.Value.X - SeekThumbWidth / 2) / w, 0, 1);
            _ = AddCueAsync((long)(ratio * len));
        };
        progressRow.GestureRecognizers.Add(seekSecondaryTap);

        var controlsRow = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            Children =
            {
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _playbackPlayPauseBtn, nextPair.Host }
                },
                new HorizontalStackLayout
                {
                    Spacing = 4,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        _playbackSpeedBtn,
                        _qualityBtn,
                        _episodesBtn,
                        settingsPair.Host,
                        _playbackMuteBtn,
                        pipPair.Host,
                        maximizePair.Host,
                        fullscreenPair.Host
                    }
                }.Also(v => Grid.SetColumn(v, 2))
            }
        };

        _playbackTransportBar = new Border
        {
            StrokeThickness = 0,
            Stroke = Colors.Transparent,
            Background = CreateTransportChromeBrush(),
            Padding = new Thickness(16, 28, 16, 14),
            VerticalOptions = LayoutOptions.End,
            StrokeShape = new RoundRectangle { CornerRadius = 0 },
            Content = new Grid
            {
                RowDefinitions =
                [
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                ],
                RowSpacing = 8,
                Children =
                {
                    progressRow,
                    controlsRow.Also(v => Grid.SetRow(v, 1)),
                    _playbackVolumePopup
                }
            }
        };

        _playbackTitleBar = new Grid
        {
            HeightRequest = 44,
            Padding = new Thickness(12, 0, 12, 8),
            VerticalOptions = LayoutOptions.Start,
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 8,
            Children =
            {
                playbackBackPair.Host,
                new HorizontalStackLayout
                {
                    Spacing = 0,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _playbackTitle, _playbackTitleDuration }
                }.Also(v => Grid.SetColumn(v, 1)),
                _pipExitBtn.Also(v => Grid.SetColumn(v, 2))
            }
        };
        _playbackTitleBarHost = new Border
        {
            HeightRequest = TitleChromeHeight,
            StrokeThickness = 0,
            Stroke = Colors.Transparent,
            Background = CreateTitleChromeBrush(),
            VerticalOptions = LayoutOptions.Start,
            StrokeShape = new RoundRectangle { CornerRadius = 0 },
            Content = _playbackTitleBar
        };

        // Win：视频全铺；chrome 进独立浮层（只创建一次），避免 inset 压缩画面
        _root = new Grid
        {
            BackgroundColor = Colors.Black,
            Children =
            {
                _playbackVideoHost,
#if !WINDOWS
                _playbackTitleBarHost,
                _playbackTransportBar,
                _menuHost
#endif
            }
        };
#if WINDOWS
        if (_playbackTransportBar.Content is Grid transportGrid &&
            !ReferenceEquals(_menuHost.Parent, transportGrid))
            transportGrid.Children.Add(_menuHost);
#endif
        var playbackPointer = new PointerGestureRecognizer();
        playbackPointer.PointerMoved += (_, _) => BumpPlaybackChrome();
        _root.GestureRecognizers.Add(playbackPointer);
        var playbackTap = new TapGestureRecognizer();
        playbackTap.Tapped += (_, _) =>
        {
            CloseMenu();
            BumpPlaybackChrome();
        };
        _root.GestureRecognizers.Add(playbackTap);

        // 点画面暂停/继续：挂在视频宿主上，避免与 chrome 按钮手势重复触发
        var videoTap = new TapGestureRecognizer();
        videoTap.Tapped += (_, _) =>
        {
            if (_seekDragging || _volumeDragging || _menuKind != MenuKind.None) return;
            PlaybackPlayPause();
        };
        _playbackVideoHost.GestureRecognizers.Add(videoTap);

        Content = _root;
        SizeChanged += (_, _) =>
        {
            if (_playbackTitleBarHost.IsVisible || _playbackTransportBar.IsVisible)
                ShowPlaybackChrome();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
        _playbackTitle.Text = title;
        _cueLengthMs = -1;
        RefreshCueLayer();
        UpdateQualityUi(null);
#if WINDOWS
        EnsureChromeOverlay();
#endif
        ShowPlaybackChrome();
        BumpPlaybackChrome();
        ReportLog($"视频墙连接中: {title}");
        await StartPlaybackAsync(sourceId);
    }

    public async Task StopAsync()
    {
        _progressTimer?.Stop();
        _chromeHideTimer?.Stop();
        CloseMenu();
        RestorePresenter();
#if WINDOWS
        ClearPlaybackRenderTarget();
        // 仅隐藏表面，保留 HWND；浮层只 Hide 不销毁
        _playbackSurface.SetSurfaceVisible(false);
        _chromeOverlay?.Hide();
#endif
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        _playbackTitle.Text = "";
        ResetSeekUi();
        _playbackTitleBarHost.IsVisible = false;
        _playbackTransportBar.IsVisible = false;
        _playbackVolumePopup.IsVisible = false;
        ApplyVideoChromeInset(title: false, transport: false);
#if WINDOWS
        _playbackSurface.Sync();
#endif
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _progressTimer = Dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        _progressTimer.Tick += (_, _) => RefreshPlaybackProgress();
        _chromeHideTimer = Dispatcher.CreateTimer();
        _chromeHideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _chromeHideTimer.Tick += (_, _) => HidePlaybackChrome();
        _volumePopupHideTimer = Dispatcher.CreateTimer();
        _volumePopupHideTimer.Interval = TimeSpan.FromMilliseconds(280);
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            if (!_volumeDragging)
                _playbackVolumePopup.IsVisible = false;
        };
        SyncSpeedButton();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _progressTimer?.Stop();
        _chromeHideTimer?.Stop();
        _volumePopupHideTimer?.Stop();
        RestorePresenter();
        DetachServiceSubscriptions();
#if WINDOWS
        ClearPlaybackRenderTarget();
        _chromeOverlay?.Dispose();
        _chromeOverlay = null;
#endif
    }

#if WINDOWS
    private void EnsureChromeOverlay()
    {
        _chromeOverlay ??= new VideoWallMauiChromeOverlay(this);
        _chromeOverlay.SetContents(_playbackTitleBarHost, _playbackTransportBar);
    }
#endif

    private void DetachServiceSubscriptions()
    {
        if (_service is null) return;
        _service.SinglePlayback.StatsChanged -= OnStatsChanged;
        _service.SinglePlayback.StateChanged -= OnPlaybackState;
    }

    private void OnStatsChanged(MediaStats stats) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateQualityUi(stats);
            if (stats.Width > 0)
                ReportLog(
                    $"视频墙播放中 {_playbackTitle.Text}: {stats.Width}×{stats.Height} @ {stats.Fps:F1} fps");
        });

    private void OnPlaybackState(TilePlayState state) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateTransportChrome(state);
            if (state is TilePlayState.Playing or TilePlayState.Paused or TilePlayState.Buffering)
                _progressTimer?.Start();
            else if (state is TilePlayState.Stopped or TilePlayState.Failed or TilePlayState.Idle)
                _progressTimer?.Stop();

            if (state is TilePlayState.Failed or TilePlayState.Stalled)
            {
                var code = _service?.SinglePlayback.ErrorCode ?? VideoErrorCode.None;
                var title = string.IsNullOrWhiteSpace(_playbackTitle.Text) ? "" : $"「{_playbackTitle.Text}」";
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

#if WINDOWS
    public void SetSurfaceVisible(bool visible) => _playbackSurface.SetSurfaceVisible(visible);

    private async Task PulseSurfaceSyncAsync()
    {
        foreach (var delay in new[] { 32, 80, 160, 320 })
        {
            await Task.Delay(delay).ConfigureAwait(true);
            if (!IsVisible) return;
            _playbackSurface.Sync();
            _chromeOverlay?.SyncBounds();
        }
    }
#endif

    private async Task StartPlaybackAsync(string sourceId)
    {
        if (_service is null) return;
        _playbackTitle.Text = _playbackTitle.Text ?? sourceId;
        try
        {
#if WINDOWS
            ClearPlaybackRenderTarget();
            _playbackSurface.SetSurfaceVisible(true);
            // 进播放页时布局常尚未完成，强等有效尺寸再绑 HWND，否则画面要拖窗口才出现
            var laidOut = await _playbackSurface.WaitUntilLaidOutAsync(2500).ConfigureAwait(true);
            var hwnd = await WaitForSurfaceHwndAsync(_playbackSurface, 1500).ConfigureAwait(true);
            if (hwnd == IntPtr.Zero || !laidOut)
            {
                await Task.Delay(80).ConfigureAwait(true);
                _playbackSurface.SetSurfaceVisible(true);
                await _playbackSurface.WaitUntilLaidOutAsync(1500).ConfigureAwait(true);
                hwnd = await WaitForSurfaceHwndAsync(_playbackSurface, 1500).ConfigureAwait(true);
            }

            if (hwnd == IntPtr.Zero)
            {
                ReportLog($"视频墙播放「{_playbackTitle.Text}」: HWND 未就绪");
                return;
            }

            await _service.SinglePlayback.PlaySourceAsync(sourceId, hwnd).ConfigureAwait(true);
            _playbackSurface.Sync();
            // 播放开始后再补几次 Sync，覆盖首帧布局抖动
            _ = PulseSurfaceSyncAsync();
#else
            await _service.SinglePlayback.PlaySourceAsync(sourceId).ConfigureAwait(true);
#endif
            UpdateTransportChrome(_service.SinglePlayback.State);
            _progressTimer?.Start();
            ReportLog($"视频墙播放: {_playbackTitle.Text}");
        }
        catch (Exception ex)
        {
#if WINDOWS
            ClearPlaybackRenderTarget();
#endif
            _progressTimer?.Stop();
            ReportLog($"视频墙播放异常「{_playbackTitle.Text}」: {ex.Message}");
        }
    }

    private async Task PlaybackBackAsync()
    {
        RestorePresenter();
        await StopAsync();
        Closed?.Invoke();
    }

    private void PlaybackPlayPause()
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

        BumpPlaybackChrome();
    }

    private void ApplySeekFromBar()
    {
        if (_service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0 || !_service.SinglePlayback.CanSeek) return;
        var ms = (long)(_playbackSeekBar.Value / SeekBarMax * len);
        _service.SinglePlayback.SeekTo(ms);
        RefreshPlaybackProgress();
    }

    private void ApplyVolumeFromBar()
    {
        if (_service is null) return;
        _service.SinglePlayback.SetVolume((int)Math.Round(_playbackVolumeBar.Value));
        _playbackVolumePct.Text = $"{(int)Math.Round(_playbackVolumeBar.Value)}%";
        UpdateMuteChrome();
    }

    private void ShowVolumePopup()
    {
        _volumePopupHideTimer?.Stop();
        _playbackVolumePopup.IsVisible = true;
        SyncVolumeUi();
    }

    private void ScheduleHideVolumePopup()
    {
        if (_volumeDragging) return;
        _volumePopupHideTimer?.Stop();
        _volumePopupHideTimer?.Start();
    }

    private void SyncSpeedButton()
    {
        var rate = SpeedSteps[Math.Clamp(_speedIndex, 0, SpeedSteps.Length - 1)];
        _playbackSpeedBtn.Text = Math.Abs(rate - 1f) < 0.01f
            ? Loc.T("Tool.VideoWall.Speed")
            : $"{rate:0.##}x";
    }

    private void RefreshPlaybackProgress()
    {
        if (_service is null || _seekDragging) return;
        var time = _service.SinglePlayback.TimeMs;
        var len = _service.SinglePlayback.LengthMs;
        _playbackTimeText.Text = FormatClock(time);
        _playbackLengthText.Text = len > 0 ? FormatClock(len) : "--:--";
        _playbackTitleDuration.Text = len > 0 ? FormatClock(len) : "";
        _playbackSeekBar.IsEnabled = _service.SinglePlayback.CanSeek && len > 0;
        if (len > 0)
            _playbackSeekBar.Value = Math.Clamp(time * SeekBarMax / len, 0, SeekBarMax);
        if (len != _cueLengthMs)
            RefreshCueLayer();
        if (!_volumeDragging)
            SyncVolumeUi();
    }

    private void ResetSeekUi()
    {
        _playbackTimeText.Text = "00:00";
        _playbackLengthText.Text = "00:00";
        _playbackSeekBar.Value = 0;
        _playbackTitleDuration.Text = "";
        _cueLengthMs = -1;
        _cueLayer.Children.Clear();
    }

    // ───────── 关键事件白点 ─────────

    /// <summary>按当前源 KeyEvents 与总时长重绘白点；无总时长（直播）时不画。</summary>
    private void RefreshCueLayer()
    {
        _cueLayer.Children.Clear();
        var len = _service?.SinglePlayback.LengthMs ?? 0;
        _cueLengthMs = len;
        if (_service is null || len <= 0) return;

        foreach (var cue in _service.SinglePlayback.GetKeyEvents())
        {
            if (cue.PositionMs < 0 || cue.PositionMs > len) continue;

            var hit = new Grid
            {
                BackgroundColor = Colors.Transparent,
                Children =
                {
                    new Ellipse
                    {
                        Fill = Colors.White,
                        WidthRequest = CueDotSize,
                        HeightRequest = CueDotSize,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        InputTransparent = true
                    }
                }
            };
            ToolTipProperties.SetText(hit, string.IsNullOrWhiteSpace(cue.Label)
                ? FormatClock(cue.PositionMs)
                : $"{FormatClock(cue.PositionMs)}  {cue.Label}");

            var captured = cue;
            var seekTap = new TapGestureRecognizer();
            seekTap.Tapped += (_, _) =>
            {
                if (_service is null || !_service.SinglePlayback.CanSeek) return;
                _service.SinglePlayback.SeekTo(captured.PositionMs);
                RefreshPlaybackProgress();
                BumpPlaybackChrome();
            };
            hit.GestureRecognizers.Add(seekTap);

            var removeTap = new TapGestureRecognizer { Buttons = ButtonsMask.Secondary };
            removeTap.Tapped += async (_, _) => await RemoveCueAsync(captured);
            hit.GestureRecognizers.Add(removeTap);

            // PositionProportional：x 比例落在 [0, 宽-子宽]，与滑块中心随值移动一致（子宽≈滑块宽）
            AbsoluteLayout.SetLayoutBounds(hit, new Rect((double)cue.PositionMs / len, 0.5, SeekThumbWidth, CueHitHeight));
            AbsoluteLayout.SetLayoutFlags(hit, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.PositionProportional);
            _cueLayer.Children.Add(hit);
        }
    }

    private async Task AddCueAsync(long positionMs)
    {
        if (_service is null) return;
        if (string.IsNullOrWhiteSpace(_service.SinglePlayback.CurrentSourceId))
        {
            ReportLog(Loc.T("Tool.VideoWall.CueNoSource"));
            return;
        }

        var current = _service.SinglePlayback.GetKeyEvents();
        // 同一位置 1 秒内视为重复
        if (current.Any(c => Math.Abs(c.PositionMs - positionMs) < 1000)) return;

        if (!await _service.SinglePlayback.SetKeyEventsAsync(current.Append(new PlaybackCue(positionMs))))
            return;
        await MainThread.InvokeOnMainThreadAsync(() =>
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
        await MainThread.InvokeOnMainThreadAsync(RefreshCueLayer);
    }

    // ───────── 清晰度 / 选集 / 设置 菜单 ─────────

    private enum MenuKind { None, Quality, Episodes, Settings }

    private static readonly Color MenuText = Color.FromArgb("#E5E7EB");
    private MenuKind _menuKind = MenuKind.None;

    private Label CreateChromeTextButton(string text, Action onTap)
    {
        var label = new Label
        {
            Text = text,
            TextColor = Colors.White,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(10, 6)
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        label.GestureRecognizers.Add(tap);
        return label;
    }

    private void ToggleMenu(MenuKind kind)
    {
        if (_menuKind == kind)
        {
            CloseMenu();
            return;
        }

        _playbackVolumePopup.IsVisible = false;
        _menuHost.Content = kind switch
        {
            MenuKind.Quality => BuildQualityMenu(),
            MenuKind.Episodes => BuildEpisodesMenu(),
            _ => BuildSettingsMenu()
        };
        _menuKind = kind;
        _menuHost.IsVisible = true;
#if WINDOWS
        // 子 HWND 会盖住 WinUI 叠层，菜单打开期间先收起视频面
        _playbackSurface.SetSurfaceVisible(false);
#endif
        BumpPlaybackChrome();
    }

    private void CloseMenu()
    {
        if (_menuKind == MenuKind.None) return;
        _menuKind = MenuKind.None;
        _menuHost.IsVisible = false;
        _menuHost.Content = null;
#if WINDOWS
        _playbackSurface.SetSurfaceVisible(true);
#endif
    }

    private static Label MenuTitle(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextColor = PlaybackTitleMuted,
        Margin = new Thickness(10, 0, 10, 4)
    };

    private static View MenuRow(string text, Action onTap, bool selected = false)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 13,
            TextColor = selected ? PlaybackAccent : MenuText,
            Padding = new Thickness(10, 7),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        label.GestureRecognizers.Add(tap);
        return label;
    }

    private static View SwitchRow(string text, bool on, Action<bool> changed)
    {
        var toggle = new Switch
        {
            IsToggled = on,
            OnColor = PlaybackAccent,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End
        };
        toggle.Toggled += (_, e) => changed(e.Value);
        return new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 16,
            Padding = new Thickness(10, 2),
            Children =
            {
                new Label { Text = text, FontSize = 13, TextColor = MenuText, VerticalOptions = LayoutOptions.Center },
                toggle.Also(v => Grid.SetColumn(v, 1))
            }
        };
    }

    private View BuildQualityMenu()
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                MenuTitle(Loc.T("Tool.VideoWall.Quality")),
                MenuRow($"{Loc.T("Tool.VideoWall.QualityAuto")}  ✓", CloseMenu, selected: true)
            }
        };
        if (_service?.SinglePlayback.LastStats is { Width: > 0, Height: > 0 } s)
        {
            stack.Children.Add(new Label
            {
                Text = $"{s.Width}×{s.Height}",
                FontSize = 12,
                TextColor = PlaybackOnBarText,
                Margin = new Thickness(10, 6, 10, 0)
            });
        }

        return stack;
    }

    private View BuildEpisodesMenu()
    {
        var list = new VerticalStackLayout { Spacing = 0 };
        if (_service is not null)
        {
            var currentId = _service.SinglePlayback.CurrentSourceId ?? _pendingPlayId;
            var sources = _service.Catalog.Sources;
            var current = sources.FirstOrDefault(s => s.Id == currentId);
            IEnumerable<VideoSourceDescriptor> items = !string.IsNullOrWhiteSpace(current?.GroupId)
                ? sources.Where(s => string.Equals(s.GroupId, current!.GroupId, StringComparison.OrdinalIgnoreCase))
                : sources;

            foreach (var src in items)
            {
                var captured = src;
                list.Children.Add(MenuRow(src.Name, () =>
                {
                    CloseMenu();
                    if (captured.Id != currentId)
                        _ = PlayAsync(captured.Id, captured.Name);
                }, selected: src.Id == currentId));
            }
        }

        return new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                MenuTitle(Loc.T("Tool.VideoWall.Episodes")),
                new ScrollView { MaximumHeightRequest = 260, Content = list }
            }
        };
    }

    private View BuildSettingsMenu()
    {
        var diag = new Label
        {
            FontSize = 11,
            TextColor = PlaybackOnBarText,
            FontFamily = "Consolas",
            IsVisible = false,
            Margin = new Thickness(10, 2, 10, 0)
        };

        return new VerticalStackLayout
        {
            Spacing = 4,
            WidthRequest = 240,
            Children =
            {
                new Label
                {
                    Text = Loc.T("Tool.VideoWall.Settings"),
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    Margin = new Thickness(10, 0, 10, 6)
                },
                SwitchRow(Loc.T("Tool.VideoWall.Loop"), _service?.SinglePlayback.Loop == true,
                    on => _service?.SinglePlayback.SetLoop(on)),
                SwitchRow(Loc.T("Tool.VideoWall.EpisodeTitle"), _showEpisodeTitle, on =>
                {
                    _showEpisodeTitle = on;
                    _playbackTitleDuration.IsVisible = on;
                }),
                new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                    Padding = new Thickness(10, 7),
                    Children =
                    {
                        new Label { Text = Loc.T("Tool.VideoWall.Aspect"), FontSize = 13, TextColor = MenuText },
                        new Label
                        {
                            Text = Loc.T("Tool.VideoWall.AspectFit"),
                            FontSize = 12,
                            TextColor = PlaybackTitleMuted
                        }.Also(v => Grid.SetColumn(v, 1))
                    }
                },
                MenuRow(Loc.T("Tool.VideoWall.AddCue"), () =>
                {
                    CloseMenu();
                    if (_service is not null)
                        _ = AddCueAsync(_service.SinglePlayback.TimeMs);
                }),
                MenuRow(Loc.T("Tool.VideoWall.RemoveCue"), () =>
                {
                    CloseMenu();
                    if (_service is null) return;
                    var now = _service.SinglePlayback.TimeMs;
                    var nearest = _service.SinglePlayback.GetKeyEvents()
                        .OrderBy(c => Math.Abs(c.PositionMs - now))
                        .FirstOrDefault();
                    if (nearest is not null && Math.Abs(nearest.PositionMs - now) <= 5_000)
                        _ = RemoveCueAsync(nearest);
                }),
                MenuRow(Loc.T("Tool.VideoWall.Diagnostics"), () =>
                {
                    diag.Text = DiagnosticsText();
                    diag.IsVisible = !diag.IsVisible;
                }),
                diag
            }
        };
    }

    private string DiagnosticsText()
    {
        var stats = _service?.SinglePlayback.LastStats;
        if (stats is null)
            return Loc.T("Tool.VideoWall.DiagnosticsNone");
        var decode = stats.HardwareDecode ? "HW" : Loc.T("Tool.VideoWall.SoftwareDecode");
        return $"{stats.Width}×{stats.Height} @ {stats.Fps:F1} fps\n" +
               $"{decode} · {stats.BufferMs} ms · {StatusTextFor(_service!.SinglePlayback.State)}";
    }

    private void UpdateQualityUi(MediaStats? stats)
        => _qualityBtn.Text = stats is { Width: > 0, Height: > 0 }
            ? $"{stats.Height}P"
            : Loc.T("Tool.VideoWall.QualityAuto");

    // ───────── 画中画 / 全屏（Maui-Win：AppWindow 展示模式；其它端仅提示） ─────────

    private void TogglePictureInPicture()
    {
        CloseMenu();
#if WINDOWS
        if (_fullscreen)
            ToggleFullscreen();
        _pip = !_pip;
        ApplyPresenter(_pip
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay
            : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        _pipExitBtn.IsVisible = _pip;
#else
        ReportLog(Loc.T("Tool.VideoWall.PictureInPicture") + ": " + Loc.T("Tool.VideoWall.DesktopOnly"));
#endif
        BumpPlaybackChrome();
    }

    private void ToggleFullscreen()
    {
        CloseMenu();
#if WINDOWS
        if (_pip)
            TogglePictureInPicture();
        _fullscreen = !_fullscreen;
        ApplyPresenter(_fullscreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        VideoWallMauiIcons.SetPathData(_fullscreenIcon,
            _fullscreen ? VideoWallMauiIcons.Geo.FullscreenExit : VideoWallMauiIcons.Geo.Fullscreen);
#else
        ReportLog(Loc.T("Tool.VideoWall.Fullscreen") + ": " + Loc.T("Tool.VideoWall.DesktopOnly"));
#endif
    }

    /// <summary>离开播放（返回/最大化/卸载）时还原窗口展示模式。</summary>
    private void RestorePresenter()
    {
        if (!_pip && !_fullscreen) return;
        _pip = false;
        _fullscreen = false;
        _pipExitBtn.IsVisible = false;
#if WINDOWS
        ApplyPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        VideoWallMauiIcons.SetPathData(_fullscreenIcon, VideoWallMauiIcons.Geo.Fullscreen);
#endif
    }

#if WINDOWS
    private void ApplyPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind kind)
    {
        try
        {
            var native = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
                as Microsoft.UI.Xaml.Window;
            native?.AppWindow.SetPresenter(kind);
        }
        catch (Exception ex)
        {
            ReportLog($"视频墙窗口模式切换失败: {ex.Message}");
        }
    }
#endif

    private void BumpPlaybackChrome()
    {
        if (!IsVisible) return;
        ShowPlaybackChrome();
        _chromeHideTimer?.Stop();
        _chromeHideTimer?.Start();
    }

    private void ShowPlaybackChrome()
    {
        var showTransport = !IsCompactChrome();
        _playbackTitleBarHost.IsVisible = true;
        _playbackTransportBar.IsVisible = showTransport;
        // 视频始终全铺，不因 chrome 显示而 inset 压缩
        ApplyVideoChromeInset(title: false, transport: false);
#if WINDOWS
        EnsureChromeOverlay();
        _chromeOverlay!.Show(title: true, transport: showTransport);
        _playbackSurface.Sync();
#endif
    }

    private void HidePlaybackChrome()
    {
        _chromeHideTimer?.Stop();
        if (!IsVisible || _seekDragging || _volumeDragging || _menuKind != MenuKind.None) return;
        _playbackTitleBarHost.IsVisible = false;
        _playbackTransportBar.IsVisible = false;
        _playbackVolumePopup.IsVisible = false;
        ApplyVideoChromeInset(title: false, transport: false);
#if WINDOWS
        _chromeOverlay?.Hide();
        _playbackSurface.Sync();
#endif
    }

    private bool IsCompactChrome() =>
        Width > 0 && Height > 0 &&
        (Width < CompactChromeThreshold || Height < CompactChromeThreshold);

    private void ApplyVideoChromeInset(bool title, bool transport)
    {
        // Maui-Win：画面全铺；chrome 由浮层叠在 HWND 上。参数保留以对齐其它端签名。
        _ = title;
        _ = transport;
        _playbackVideoHost.Margin = new Thickness(0);
    }

    private static LinearGradientBrush CreateTitleChromeBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            // 深色为主，靠向视频一侧再淡出（避免浅色雾白）
            new GradientStop(Color.FromArgb("#F20B1220"), 0f),
            new GradientStop(Color.FromArgb("#CC0B1220"), 0.55f),
            new GradientStop(Color.FromArgb("#000B1220"), 1f)
        }
    };

    private static LinearGradientBrush CreateTransportChromeBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb("#000B1220"), 0f),
            new GradientStop(Color.FromArgb("#990B1220"), 0.35f),
            new GradientStop(Color.FromArgb("#E60B1220"), 0.75f),
            new GradientStop(Color.FromArgb("#F20B1220"), 1f)
        }
    };

    private void UpdateTransportChrome(TilePlayState state)
    {
        var pausedOrStopped = state is TilePlayState.Paused or TilePlayState.Stopped
            or TilePlayState.Idle or TilePlayState.Failed;
        VideoWallMauiIcons.SetPathData(_playbackPlayPauseIcon,
            pausedOrStopped ? VideoWallMauiIcons.Geo.Play : VideoWallMauiIcons.Geo.Pause);
        var playHint = Loc.T(pausedOrStopped
            ? state == TilePlayState.Paused ? "Tool.VideoWall.Resume" : "Tool.VideoWall.Play"
            : "Tool.VideoWall.Pause");
        SemanticProperties.SetHint(_playbackPlayPauseBtn, playHint);
        AutomationProperties.SetName(_playbackPlayPauseBtn, playHint);
        UpdateMuteChrome();
        SyncVolumeUi();
        RefreshPlaybackProgress();
    }

    private void SyncVolumeUi()
    {
        if (_service is null || _volumeDragging) return;
        var vol = _service.SinglePlayback.Volume;
        if (Math.Abs(_playbackVolumeBar.Value - vol) > 0.5)
            _playbackVolumeBar.Value = vol;
        _playbackVolumePct.Text = $"{vol}%";
        UpdateMuteChrome();
    }

    private void UpdateMuteChrome()
    {
        var muted = _service?.SinglePlayback.IsMuted == true;
        VideoWallMauiIcons.SetPathData(_playbackMuteIcon,
            muted ? VideoWallMauiIcons.Geo.Mute : VideoWallMauiIcons.Geo.Volume);
        var hint = Loc.T(muted ? "Tool.VideoWall.Unmute" : "Tool.VideoWall.Mute");
        SemanticProperties.SetHint(_playbackMuteBtn, hint);
        AutomationProperties.SetName(_playbackMuteBtn, hint);
    }

#if WINDOWS
    private static async Task<IntPtr> WaitForSurfaceHwndAsync(VideoWallHwndSurface surface, int timeoutMs = 1000)
    {
        if (surface.Hwnd != IntPtr.Zero)
            return surface.Hwnd;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHwndReady() => ready.TrySetResult();
        surface.HwndReady += OnHwndReady;
        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (surface.Hwnd == IntPtr.Zero && Environment.TickCount64 < deadline)
            {
                var waitMs = (int)Math.Min(50, deadline - Environment.TickCount64);
                if (waitMs <= 0)
                    break;
                var completed = await Task.WhenAny(ready.Task, Task.Delay(waitMs)).ConfigureAwait(true);
                if (completed == ready.Task || surface.Hwnd != IntPtr.Zero)
                    break;
            }

            return surface.Hwnd;
        }
        finally
        {
            surface.HwndReady -= OnHwndReady;
        }
    }

    private void ClearPlaybackRenderTarget() => _service?.SinglePlayback.SetRenderTarget(IntPtr.Zero);
#endif

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

    private static string FormatClock(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

file static class VideoWallPlayerControlExt
{
    public static T Also<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }
}