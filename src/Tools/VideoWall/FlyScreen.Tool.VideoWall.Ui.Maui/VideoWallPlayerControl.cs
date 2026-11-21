using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Maui;
using Microsoft.Maui.Controls.Shapes;
using Grid = Microsoft.Maui.Controls.Grid;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

public sealed class VideoWallPlayerControl : ContentView
{
    private const long SeekStepMs = 5_000;
    private const double SeekBarMax = 1000;
    private const double SeekThumbWidth = 20;
    private const int SeekHoldMaxMs = 15_000;
    private const int SeekCatchupMs = 400;
    private const int SeekCatchupHoldMs = 400;
    private const double CueHitHeight = 16;
    private const double CueDotSize = 6;
    private const double CompactChromeThreshold = 300;
    private const double TitleChromeHeight = 56;
    private const double TransportChromeHeight = 128;
    private static readonly float[] SpeedSteps = [0.75f, 1f, 1.25f, 1.5f, 2f];

    private static readonly Color PlaybackAccent = Color.FromArgb("#00D4C8");
    private static readonly Color PlaybackOnBarText = Color.FromArgb("#CCE5E7EB");
    private static readonly Color PlaybackTitleMuted = Color.FromArgb("#94A3B8");

    private readonly Grid _root;
    private readonly Label _playbackTitle;
    private readonly Border _playbackVideoHost;
    private readonly LoadingOverlay _playbackLoading;
    private readonly VideoWallFrameSurface _playbackSurface;
    private readonly Border _playbackPlayPauseBtn;
    private readonly Path _playbackPlayPauseIcon;
    private readonly Slider _playbackSeekBar;
    private readonly Slider _playbackVolumeBar;
    private readonly Label _playbackVolumePct;
    private readonly Label _playbackTimeText;
    private readonly Label _playbackLengthText;
    private readonly Border _playbackMuteBtn;
    private readonly Path _playbackMuteIcon;
    private readonly Grid _playbackVolumeHost;
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
    private readonly Grid _controlsRow;
    private readonly HorizontalStackLayout _leftChromeRow;
    private readonly HorizontalStackLayout _rightChromeRow;
    private readonly Border _moreBtn;
    private readonly List<ChromeOverflowItem> _chromeOverflowItems = [];
    private readonly List<ChromeOverflowItem> _chromeOverflowed = [];

    private IVideoWallService? _service;
    private string? _pendingPlayId;
    private bool _hasPresentedFrame;
    private bool _seekDragging;
    private long _seekHoldMs = -1;
    private long _seekHoldUntilTick;
    private long _seekHoldNearSinceTick = -1;
    private bool _volumeDragging;
    private bool _volumePopupHover;
    private bool _progressUpdating;
    private int _speedIndex = 1;
    private long _cueLengthMs = -1;
    private bool _showEpisodeTitle = true;
    private FloatMode _floatMode;
    private VideoWallMauiFloatHost? _floatHost;
    private Border? _floatPlaceholder;
    private bool _volumePopupAnimating;
    private IDispatcherTimer? _progressTimer;
    private IDispatcherTimer? _chromeHideTimer;
    private IDispatcherTimer? _volumePopupHideTimer;

    private enum FloatMode { None, Pip, Maximized, Fullscreen }

    public Action<string>? StatusReporter { get; set; }
    public event Action? Closed;

    /// <summary>应用内最大化：由面板把当前源所在的墙格铺满整墙（保留兼容；播放器最大化按钮改控浮窗）。</summary>
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
            // 勿 FontFamily=Consolas：Maui FontManager 会探测 Assets/Fonts 相对 URI 报错
            StyleClass = ["Mono"],
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var playbackBackPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Back, Loc.T("Tool.VideoWall.Back"),
            margin: new Thickness(0), iconSize: 18);
        VideoWallMauiIcons.WireClicked(playbackBackPair.Host, async (_, _) => await PlaybackBackAsync());

        // 软件帧 Image：各端统一；Win 优先 WriteableBitmap，手机走 BMP
        _playbackSurface = new VideoWallFrameSurface
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        _playbackVideoHost = new Border
        {
            BackgroundColor = Colors.Black,
            Stroke = Colors.Transparent,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 0 },
            Padding = 0,
            Margin = 0,
            Content = _playbackSurface
        };

        var playPausePair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Pause, Loc.T("Tool.VideoWall.Pause"), iconSize: 22);
        _playbackPlayPauseBtn = playPausePair.Host;
        _playbackPlayPauseIcon = playPausePair.Icon;
        VideoWallMauiIcons.WireClicked(_playbackPlayPauseBtn, (_, _) => PlaybackPlayPause());

        var nextPair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.Next, Loc.T("Tool.VideoWall.SeekForward5"), iconSize: 20);
        VideoWallMauiIcons.WireClicked(nextPair.Host, (_, _) =>
        {
            if (_service is null) return;
            _service.SinglePlayback.SeekBy(SeekStepMs);
            HoldSeekUi(_service.SinglePlayback.TimeMs);
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
        // 悬停音量图标弹出竖条（对齐 Win MouseEnter）
        _playbackVolumeHost = new Grid
        {
            WidthRequest = 36,
            HeightRequest = 34,
            Children = { _playbackMuteBtn }
        };
        var volumeHostPtr = new PointerGestureRecognizer();
        volumeHostPtr.PointerEntered += (_, _) =>
        {
            _volumePopupHover = true;
            ShowVolumePopup();
            BumpPlaybackChrome();
        };
        volumeHostPtr.PointerExited += (_, _) =>
        {
            _volumePopupHover = false;
            ScheduleHideVolumePopup();
        };
        _playbackVolumeHost.GestureRecognizers.Add(volumeHostPtr);

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
        VideoWallMauiIcons.WireClicked(maximizePair.Host, (_, _) => ToggleMaximizeFloat());

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
            ZIndex = 20,
            Opacity = 0,
            TranslationY = 18,
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
        // 指针在弹层上时保持显示，移出后再收起
        var volumePopupPtr = new PointerGestureRecognizer();
        volumePopupPtr.PointerEntered += (_, _) =>
        {
            _volumePopupHover = true;
            _volumePopupHideTimer?.Stop();
            ShowVolumePopup();
        };
        volumePopupPtr.PointerExited += (_, _) =>
        {
            _volumePopupHover = false;
            ScheduleHideVolumePopup();
        };
        _playbackVolumePopup.GestureRecognizers.Add(volumePopupPtr);

        _playbackSpeedBtn = new Label
        {
            Text = Loc.T("Tool.VideoWall.Speed"),
            TextColor = Colors.White,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(10, 6),
            BackgroundColor = Colors.Transparent
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
        VideoWallMauiIcons.WireChromeHover(_playbackSpeedBtn);

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
            Margin = new Thickness(0, 0, 12, TransportChromeHeight - 8),
            MinimumWidthRequest = 160,
            MaximumWidthRequest = 300,
            // 高于 transport/title，避免被底栏盖住；内容过高时靠 ScrollView 收纳
            ZIndex = 40
        };
        // 点菜单本身不要冒泡成「点空白关闭」
        _menuHost.GestureRecognizers.Add(new TapGestureRecognizer());

        _playbackTimeText = new Label
        {
            Text = "00:00",
            FontSize = 11,
            TextColor = PlaybackOnBarText,
            StyleClass = ["Mono"],
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.End
        };
        _playbackLengthText = new Label
        {
            Text = "00:00",
            FontSize = 11,
            TextColor = PlaybackOnBarText,
            StyleClass = ["Mono"],
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
            if (_service is null || _progressUpdating) return;
            var len = _service.SinglePlayback.LengthMs;
            if (len <= 0) len = _cueLengthMs;
            if (len <= 0) return;
            var ms = (long)(e.NewValue / SeekBarMax * len);
            _playbackTimeText.Text = FormatClock(ms);
            if (_seekDragging)
            {
                // 拖动中只预览，松手再 Seek
                HoldSeekUi(ms);
                return;
            }

            // 点击轨道常无 Drag 会话，须在 ValueChanged 里直接 Seek
            HoldSeekUi(ms);
            _service.SinglePlayback.SeekTo(ms);
            BumpPlaybackChrome();
        };
        // 部分平台点击不改 Value：按坐标比例跳转
        var seekPrimaryTap = new TapGestureRecognizer { Buttons = ButtonsMask.Primary };
        seekPrimaryTap.Tapped += (_, e) => SeekByPointer(e.GetPosition(_playbackSeekBar));
        _playbackSeekBar.GestureRecognizers.Add(seekPrimaryTap);

        // 关键事件白点层：穿透点击，避免挡住进度条点击/拖动
        _cueLayer = new AbsoluteLayout
        {
            HeightRequest = CueHitHeight,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 8),
            InputTransparent = true
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

        _leftChromeRow = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _playbackPlayPauseBtn, nextPair.Host }
        };

        var morePair = VideoWallMauiIcons.CreateChromeIconButton(
            VideoWallMauiIcons.Geo.More, Loc.T("Tool.VideoWall.More"),
            margin: new Thickness(0), iconSize: 18);
        _moreBtn = morePair.Host;
        _moreBtn.IsVisible = false;
        VideoWallMauiIcons.WireClicked(_moreBtn, (_, _) => ToggleMenu(MenuKind.More));

        // 溢出时优先收左侧次要项；音量/全屏尽量留在栏上
        _chromeOverflowItems.AddRange(
        [
            new ChromeOverflowItem(_playbackSpeedBtn, () => _playbackSpeedBtn.Text ?? Loc.T("Tool.VideoWall.Speed"), () =>
            {
                _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
                _service?.SinglePlayback.SetRate(SpeedSteps[_speedIndex]);
                SyncSpeedButton();
                BumpPlaybackChrome();
            }, 56),
            new ChromeOverflowItem(_qualityBtn, () => Loc.T("Tool.VideoWall.Quality"), () => ToggleMenu(MenuKind.Quality), 52),
            new ChromeOverflowItem(_episodesBtn, () => Loc.T("Tool.VideoWall.Episodes"), () => ToggleMenu(MenuKind.Episodes), 48),
            new ChromeOverflowItem(settingsPair.Host, () => Loc.T("Tool.VideoWall.Settings"), () => ToggleMenu(MenuKind.Settings), 38,
                VideoWallMauiIcons.Geo.Settings),
            new ChromeOverflowItem(_playbackVolumeHost, () => Loc.T(
                    _service?.SinglePlayback.IsMuted == true ? "Tool.VideoWall.Unmute" : "Tool.VideoWall.Mute"),
                () =>
                {
                    _service?.SinglePlayback.ToggleMute();
                    SyncVolumeUi();
                    ShowVolumePopup();
                    BumpPlaybackChrome();
                }, 38, VideoWallMauiIcons.Geo.Volume),
            new ChromeOverflowItem(pipPair.Host, () => Loc.T("Tool.VideoWall.PictureInPicture"), () => TogglePictureInPicture(), 38,
                VideoWallMauiIcons.Geo.Pip),
            new ChromeOverflowItem(maximizePair.Host, () => Loc.T(
                    _floatMode == FloatMode.Maximized ? "Tool.VideoWall.MaximizeExit" : "Tool.VideoWall.Maximize"),
                () => ToggleMaximizeFloat(), 38, VideoWallMauiIcons.Geo.Maximize),
            new ChromeOverflowItem(fullscreenPair.Host, () => Loc.T(
                    _floatMode == FloatMode.Fullscreen ? "Tool.VideoWall.FullscreenExit" : "Tool.VideoWall.Fullscreen"),
                () =>
                {
                    ToggleFullscreen();
                    BumpPlaybackChrome();
                }, 38, VideoWallMauiIcons.Geo.Fullscreen)
        ]);

        _rightChromeRow = new HorizontalStackLayout
        {
            Spacing = 4,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End
        };
        foreach (var item in _chromeOverflowItems)
            _rightChromeRow.Children.Add(item.View);
        _rightChromeRow.Children.Add(_moreBtn);

        // 右栏必须用 * 吃剩余宽度：Auto|Auto 在窄屏会按「理想宽度」撑开并与左栏重叠，
        // SizeChanged 也会读到虚高 Width，导致永远不进入「更多」。
        _controlsRow = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            ],
            ColumnSpacing = 8,
            Children =
            {
                _leftChromeRow,
                new Grid
                {
                    HorizontalOptions = LayoutOptions.Fill,
                    IsClippedToBounds = true,
                    Children = { _rightChromeRow }
                }.Also(v => Grid.SetColumn(v, 1))
            }
        };
        _controlsRow.SizeChanged += (_, _) => RelayoutChromeOverflow();
        SizeChanged += (_, _) => RelayoutChromeOverflow();
        // 手机不等待 SizeChanged：立刻收进「更多」，避免首帧闪出一排图标
        if (DeviceInfo.Idiom == DeviceIdiom.Phone
            || DeviceInfo.Platform == DevicePlatform.Android)
            ApplyChromeOverflowAllToMore();

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
                    _controlsRow.Also(v => Grid.SetRow(v, 1)),
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

        _playbackLoading = new LoadingOverlay();

        // 画面全铺 + chrome 叠层（软件帧，非 HWND）；禁止 inset，避免压画面/假透明
        _root = new Grid
        {
            BackgroundColor = Colors.Black,
            // 菜单贴底向上伸展时勿裁切；过高项走 ScrollView
            IsClippedToBounds = false,
            Children =
            {
                _playbackVideoHost,
                _playbackLoading,
                _playbackTitleBarHost,
                _playbackTransportBar,
                _menuHost
            }
        };
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
        _hasPresentedFrame = false;
        SetLoadingOverlay(true, Loc.T("Tool.VideoWall.StatusConnecting"));
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
        _playbackSurface.Clear();
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        _hasPresentedFrame = false;
        _playbackTitle.Text = "";
        ResetSeekUi();
        SetLoadingOverlay(false);
        _playbackTitleBarHost.IsVisible = false;
        _playbackTransportBar.IsVisible = false;
        _playbackVolumePopup.IsVisible = false;
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
            if (!_volumeDragging && !_volumePopupHover)
                _ = HideVolumePopupAnimatedAsync();
        };
        SyncSpeedButton();
        // 布局完成后再算溢出，避免首帧 Width 未就绪
        Dispatcher.Dispatch(() => RelayoutChromeOverflow());
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _progressTimer?.Stop();
        _chromeHideTimer?.Stop();
        _volumePopupHideTimer?.Stop();
        RestorePresenter();
        DetachServiceSubscriptions();
        _playbackSurface.Clear();
    }

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
            if (stats.Width > 0 || stats.FirstFrameMs >= 0)
                MarkFramePresented();
            if (stats.Width > 0)
                ReportLog(
                    $"视频墙播放中 {_playbackTitle.Text}: {stats.Width}×{stats.Height} @ {stats.Fps:F1} fps");
        });

    private void OnPlaybackState(TilePlayState state) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateTransportChrome(state);
            if (state is TilePlayState.Playing)
                _hasPresentedFrame = true;
            UpdateLoadingFromState(state);
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

    private void MarkFramePresented()
    {
        if (_hasPresentedFrame) return;
        _hasPresentedFrame = true;
        UpdateLoadingFromState(_service?.SinglePlayback.State ?? TilePlayState.Idle);
    }

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
            _playbackLoading.Show(string.IsNullOrWhiteSpace(text)
                ? Loc.T("Tool.VideoWall.StatusLoading")
                : text);
        else
            _playbackLoading.Hide();
    }

    private void ReportLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusReporter?.Invoke(message);
    }

    private async Task StartPlaybackAsync(string sourceId)
    {
        if (_service is null) return;
        _playbackTitle.Text = _playbackTitle.Text ?? sourceId;
        try
        {
            ShowPlaybackChrome();
            _playbackSurface.Clear();
            // 软件帧回调出画（Win / Android / 其它 Maui 宿主）
            await _service.SinglePlayback
                .PlaySourceAsync(sourceId, renderTarget: null, frameSink: _playbackSurface)
                .ConfigureAwait(true);
            UpdateTransportChrome(_service.SinglePlayback.State);
            _progressTimer?.Start();
            ReportLog($"视频墙播放: {_playbackTitle.Text}");
        }
        catch (Exception ex)
        {
            _playbackSurface.Clear();
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
        if (len <= 0) len = _cueLengthMs;
        if (len <= 0) return;
        var ms = (long)(_playbackSeekBar.Value / SeekBarMax * len);
        HoldSeekUi(ms);
        _service.SinglePlayback.SeekTo(ms);
    }

    private void SeekByPointer(Point? position)
    {
        if (position is null || _service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0) len = _cueLengthMs;
        if (len <= 0) return;
        var w = Math.Max(1, _playbackSeekBar.Width - SeekThumbWidth);
        var ratio = Math.Clamp((position.Value.X - SeekThumbWidth / 2) / w, 0, 1);
        var ms = (long)(ratio * len);
        _progressUpdating = true;
        try
        {
            _playbackSeekBar.Value = ratio * SeekBarMax;
        }
        finally
        {
            _progressUpdating = false;
        }

        HoldSeekUi(ms);
        _service.SinglePlayback.SeekTo(ms);
        BumpPlaybackChrome();
    }

    private void HoldSeekUi(long ms)
    {
        _seekHoldMs = ms;
        _seekHoldUntilTick = Environment.TickCount64 + SeekHoldMaxMs;
        _seekHoldNearSinceTick = -1;
        _playbackTimeText.Text = FormatClock(ms);
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
        SyncVolumeUi();
        if (_playbackVolumePopup.IsVisible && !_volumePopupAnimating && _playbackVolumePopup.Opacity >= 0.99)
            return;
        _playbackVolumePopup.IsVisible = true;
        _ = AnimateVolumePopupAsync(show: true);
    }

    private async Task HideVolumePopupAnimatedAsync()
    {
        if (!_playbackVolumePopup.IsVisible) return;
        await AnimateVolumePopupAsync(show: false);
        if (_volumePopupHover || _volumeDragging) return;
        _playbackVolumePopup.IsVisible = false;
    }

    private async Task AnimateVolumePopupAsync(bool show)
    {
        _volumePopupAnimating = true;
        try
        {
            if (show)
            {
                await Task.WhenAll(
                    _playbackVolumePopup.FadeToAsync(1, 180, Easing.CubicOut),
                    _playbackVolumePopup.TranslateToAsync(0, 0, 180, Easing.CubicOut));
            }
            else
            {
                await Task.WhenAll(
                    _playbackVolumePopup.FadeToAsync(0, 150, Easing.CubicIn),
                    _playbackVolumePopup.TranslateToAsync(0, 18, 150, Easing.CubicIn));
            }
        }
        catch { /* 卸载竞态 */ }
        finally { _volumePopupAnimating = false; }
    }

    private void ScheduleHideVolumePopup()
    {
        if (_volumeDragging || _volumePopupHover) return;
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

        _playbackTimeText.Text = FormatClock(time);
        _playbackLengthText.Text = len > 0 ? FormatClock(len) : "--:--";
        _playbackTitleDuration.Text = len > 0 ? FormatClock(len) : "";
        _playbackSeekBar.IsEnabled = len > 0;
        if (len > 0)
        {
            _progressUpdating = true;
            try
            {
                _playbackSeekBar.Value = Math.Clamp(time * SeekBarMax / len, 0, SeekBarMax);
            }
            finally
            {
                _progressUpdating = false;
            }
        }

        if (len != _cueLengthMs)
            RefreshCueLayer();
        if (!_volumeDragging)
            SyncVolumeUi();
    }

    private void ResetSeekUi()
    {
        _seekHoldMs = -1;
        _seekHoldNearSinceTick = -1;
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
                InputTransparent = false,
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
                HoldSeekUi(captured.PositionMs);
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

    private enum MenuKind { None, Quality, Episodes, Settings, More }

    private sealed class ChromeOverflowItem(
        View view,
        Func<string> label,
        Action activate,
        double minWidth,
        string? iconGeo = null)
    {
        public View View { get; } = view;
        public Func<string> Label { get; } = label;
        public Action Activate { get; } = activate;
        public double MinWidth { get; } = minWidth;
        public string? IconGeo { get; } = iconGeo;
    }

    /// <summary>
    /// 右侧操作栏放不下时，把靠左的次要按钮收进「更多」；够宽时保持原样全展开。
    /// 手机：一律「播放+下一首 | 更多」（MAUI 虚高 Width 会使预算永远够，永远不折叠）。
    /// </summary>
    private void RelayoutChromeOverflow()
    {
        if (_chromeOverflowItems.Count == 0) return;

        // 手机 / Android：强制更多菜单，不依赖宽度测量
        var forceMore = DeviceInfo.Idiom == DeviceIdiom.Phone
            || DeviceInfo.Platform == DevicePlatform.Android;
        if (forceMore)
        {
            ApplyChromeOverflowAllToMore();
            return;
        }

        // 播放器/控件行实宽（扣除左右 padding）
        var hostW = Width > 1 ? Width
            : _playbackTransportBar.Width > 1 ? _playbackTransportBar.Width
            : _controlsRow.Width;
        if (hostW <= 1) return;

        var pad = _playbackTransportBar.Padding.Left + _playbackTransportBar.Padding.Right;
        var leftW = _leftChromeRow.Width > 1 ? _leftChromeRow.Width : 80;
        var budget = Math.Max(0, hostW - pad - leftW - 16);
        const double moreW = 40;
        const double gap = 4;

        // 一律用估算宽度：重叠时 View.Width 不可信
        double Est(ChromeOverflowItem it)
            => it.MinWidth + it.View.Margin.Left + it.View.Margin.Right;

        var widths = _chromeOverflowItems.Select(Est).ToArray();
        var total = widths.Sum() + gap * Math.Max(0, _chromeOverflowItems.Count - 1);

        _chromeOverflowed.Clear();
        var needMore = total > budget + 0.5;
        if (needMore)
        {
            var remaining = total + moreW + gap;
            for (var i = 0; i < _chromeOverflowItems.Count && remaining > budget; i++)
            {
                _chromeOverflowed.Add(_chromeOverflowItems[i]);
                remaining -= widths[i] + gap;
            }

            // 至少留一个在栏上（全屏）；若预算连「更多+一个」都不够，则全部进更多
            if (_chromeOverflowed.Count >= _chromeOverflowItems.Count && budget >= moreW + 38)
                _chromeOverflowed.RemoveAt(_chromeOverflowed.Count - 1);
        }

        ApplyChromeOverflowVisibility();
    }

    private void ApplyChromeOverflowAllToMore()
    {
        _chromeOverflowed.Clear();
        _chromeOverflowed.AddRange(_chromeOverflowItems);
        ApplyChromeOverflowVisibility();
    }

    private void ApplyChromeOverflowVisibility()
    {
        _rightChromeRow.Children.Clear();
        foreach (var item in _chromeOverflowItems)
        {
            var hidden = _chromeOverflowed.Contains(item);
            item.View.IsVisible = !hidden;
            if (!hidden)
                _rightChromeRow.Children.Add(item.View);
        }

        var showMore = _chromeOverflowed.Count > 0;
        _moreBtn.IsVisible = showMore;
        if (showMore)
            _rightChromeRow.Children.Add(_moreBtn);

        if (_menuKind == MenuKind.More && _chromeOverflowed.Count == 0)
            CloseMenu();
        else if (_menuKind == MenuKind.More)
            _menuHost.Content = BuildMoreMenu();
    }

    private View BuildMoreMenu()
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            Children = { MenuTitle(Loc.T("Tool.VideoWall.More")) }
        };
        foreach (var item in _chromeOverflowed)
        {
            var captured = item;
            stack.Children.Add(MenuRow(captured.Label(), () =>
            {
                CloseMenu();
                captured.Activate();
            }, iconGeo: captured.IconGeo));
        }

        return WrapMenuScroll(stack);
    }

    /// <summary>窄屏「更多」项多：限制最大高度并可滚动，避免被播放器底边裁切。</summary>
    private View WrapMenuScroll(View content)
    {
        var chrome = TitleChromeHeight + TransportChromeHeight + 24;
        var avail = Height > chrome ? Height - chrome : 0;
        // 未完成布局时给一个够用的兜底；有高度则留出标题/底栏
        var maxH = avail > 80 ? avail : 320;
        maxH = Math.Clamp(maxH, 140, 420);
        _menuHost.MaximumHeightRequest = maxH + 24; // border padding
        return new ScrollView
        {
            MaximumHeightRequest = maxH,
            VerticalOptions = LayoutOptions.Start,
            HorizontalOptions = LayoutOptions.Fill,
            Content = content
        };
    }

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
            Padding = new Thickness(10, 6),
            BackgroundColor = Colors.Transparent
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        label.GestureRecognizers.Add(tap);
        VideoWallMauiIcons.WireChromeHover(label);
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
        // Episodes 内部已有 ScrollView，勿再套一层
        _menuHost.Content = kind switch
        {
            MenuKind.Quality => WrapMenuScroll(BuildQualityMenu()),
            MenuKind.Episodes => BuildEpisodesMenu(),
            MenuKind.More => BuildMoreMenu(),
            _ => WrapMenuScroll(BuildSettingsMenu())
        };
        _menuKind = kind;
        _menuHost.IsVisible = true;
        BumpPlaybackChrome();
    }

    private void CloseMenu()
    {
        if (_menuKind == MenuKind.None) return;
        _menuKind = MenuKind.None;
        _menuHost.IsVisible = false;
        _menuHost.Content = null;
        _menuHost.MaximumHeightRequest = double.PositiveInfinity;
    }

    private static Label MenuTitle(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextColor = PlaybackTitleMuted,
        Margin = new Thickness(10, 0, 10, 4)
    };

    private static View MenuRow(string text, Action onTap, bool selected = false, string? iconGeo = null)
    {
        var color = selected ? PlaybackAccent : MenuText;
        View content;
        if (string.IsNullOrEmpty(iconGeo))
        {
            content = new Label
            {
                Text = text,
                FontSize = 13,
                TextColor = color,
                Padding = new Thickness(10, 7),
                LineBreakMode = LineBreakMode.TailTruncation
            };
        }
        else
        {
            content = new HorizontalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(10, 7),
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    VideoWallMauiIcons.CreatePath(iconGeo, color, 16),
                    new Label
                    {
                        Text = text,
                        FontSize = 13,
                        TextColor = color,
                        VerticalOptions = LayoutOptions.Center,
                        LineBreakMode = LineBreakMode.TailTruncation
                    }
                }
            };
        }

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        content.GestureRecognizers.Add(tap);
        return content;
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
            StyleClass = ["Mono"],
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

    // ───────── 画中画 / 最大化 / 全屏（面板 AbsoluteLayout 浮窗，手机与电脑共用） ─────────

    private void TogglePictureInPicture()
    {
        CloseMenu();
        if (_floatMode != FloatMode.None)
            CloseFloatHost();
        else
            ApplyFloatMode(FloatMode.Pip);
        BumpPlaybackChrome();
    }

    private void ToggleMaximizeFloat()
    {
        CloseMenu();
        if (_floatMode == FloatMode.Maximized)
            ApplyFloatMode(FloatMode.Pip);
        else
            ApplyFloatMode(FloatMode.Maximized);
        BumpPlaybackChrome();
    }

    private void ToggleFullscreen()
    {
        CloseMenu();
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
            CloseFloatHost();
            return;
        }

        var host = EnsureFloatHost();
        if (host is null)
        {
            ReportLog(Loc.T("Tool.VideoWall.PictureInPicture") + ": AbsoluteLayout host missing");
            return;
        }

        var mauiMode = mode switch
        {
            FloatMode.Maximized => VideoWallMauiFloatHost.Mode.Maximized,
            FloatMode.Fullscreen => VideoWallMauiFloatHost.Mode.Fullscreen,
            _ => VideoWallMauiFloatHost.Mode.Pip
        };

        if (!host.IsOpen)
        {
            _floatPlaceholder ??= new Border { BackgroundColor = Colors.Black, MinimumHeightRequest = 120 };
            // 先迁出 _root（FloatHost 会清空 Content），再放占位，避免布局塌陷
            host.Open(_root, mauiMode);
            Content = _floatPlaceholder;
        }
        else
        {
            host.ApplyMode(mauiMode, resetPipBounds: mode == FloatMode.Pip);
        }

        _floatMode = mode;
        _pipExitBtn.IsVisible = true;
        UpdateFloatChrome();
        ShowPlaybackChrome();
    }

    private VideoWallMauiFloatHost? EnsureFloatHost()
    {
        if (_floatHost is not null) return _floatHost;
        AbsoluteLayout? layer = null;
        for (Element? p = Parent; p != null; p = p.Parent)
        {
            if (p is AbsoluteLayout abs)
            {
                layer = abs;
                break;
            }
        }

        if (layer is null) return null;
        _floatHost = new VideoWallMauiFloatHost(layer);
        return _floatHost;
    }

    private void CloseFloatHost()
    {
        _chromeHideTimer?.Stop();
        if (_floatHost?.IsOpen == true)
            _floatHost.Close();
        if (!ReferenceEquals(Content, _root))
            Content = _root;
        _floatMode = FloatMode.None;
        _pipExitBtn.IsVisible = false;
        UpdateFloatChrome();
        ShowPlaybackChrome();
    }

    /// <summary>离开播放（返回/卸载）时收起浮窗。</summary>
    private void RestorePresenter()
    {
        if (_floatMode == FloatMode.None) return;
        CloseFloatHost();
    }

    private void UpdateFloatChrome()
    {
        VideoWallMauiIcons.SetPathData(_fullscreenIcon,
            _floatMode == FloatMode.Fullscreen
                ? VideoWallMauiIcons.Geo.FullscreenExit
                : VideoWallMauiIcons.Geo.Fullscreen);
        SemanticProperties.SetHint(_pipExitBtn, Loc.T(
            _floatMode != FloatMode.None
                ? "Tool.VideoWall.PictureInPictureExit"
                : "Tool.VideoWall.PictureInPicture"));
    }

    private void BumpPlaybackChrome()
    {
        if (!IsVisible && _floatMode == FloatMode.None) return;
        ShowPlaybackChrome();
        _chromeHideTimer?.Stop();
        _chromeHideTimer?.Start();
    }

    private void ShowPlaybackChrome()
    {
        var showTitle = _floatMode != FloatMode.Fullscreen;
        var showTransport = _floatMode == FloatMode.Fullscreen || !IsCompactChrome();
        _playbackTitleBarHost.IsVisible = showTitle;
        _playbackTransportBar.IsVisible = showTransport;
        _playbackVideoHost.Margin = new Thickness(0);
    }

    private void HidePlaybackChrome()
    {
        _chromeHideTimer?.Stop();
        if ((!IsVisible && _floatMode == FloatMode.None)
            || _seekDragging || _volumeDragging || _volumePopupHover || _menuKind != MenuKind.None)
            return;

        _ = HideVolumePopupAnimatedAsync();

        if (_floatMode == FloatMode.Maximized)
        {
            _playbackTitleBarHost.IsVisible = true;
            _playbackTransportBar.IsVisible = false;
            return;
        }

        _playbackTitleBarHost.IsVisible = false;
        _playbackTransportBar.IsVisible = false;
    }

    // 仅高度过矮时收起整条操作栏；窄宽度改走「更多」溢出，避免手机竖屏看不到控制
    private bool IsCompactChrome() =>
        Height > 0 && Height < CompactChromeThreshold;

    private static LinearGradientBrush CreateTitleChromeBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb("#CC0B1220"), 0f),
            new GradientStop(Color.FromArgb("#880B1220"), 0.55f),
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
            new GradientStop(Color.FromArgb("#660B1220"), 0.35f),
            new GradientStop(Color.FromArgb("#AA0B1220"), 0.75f),
            new GradientStop(Color.FromArgb("#DD0B1220"), 1f)
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