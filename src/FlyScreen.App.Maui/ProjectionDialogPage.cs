using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;
#if WINDOWS
using FlyScreen.ScreenExtend;
#endif

namespace FlyScreen.AppMaui;

/// <summary>投影弹窗：画面/选项/环境/卸载合并进可折叠「选项」（默认收起）；环境态在标题栏；手机仅复制。</summary>
public sealed class ProjectionDialogPage : ContentPage
{
    private readonly MauiAppHost _host;
    private readonly bool _phoneOnlyClone;
    private readonly Picker _modePicker = new();
    private readonly Picker _targetPicker = new();
    private readonly Picker _resolutionPicker = new();
    private readonly Picker _scalePicker = new();
    private readonly Picker _fpsPicker = new();
    private readonly Picker _qualityPicker = new();
    private readonly List<Label> _fieldCaptions = [];
    private readonly Label _modeHint = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _targetHint = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _displayHint = new()
    {
        FontSize = 12,
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "复制默认本机主屏，扩展默认目标电脑。帧率和画质越高越清晰，也更吃编码和网络。"
    };
    private readonly List<(ProjectionDisplaySource Source, int W, int H, int Hz)> _resOptions = [];
    private int _displaySeq;
    private bool _suppressDisplayChange;
    private bool _displaySelectionReady;
    private DisplayInfoPayload? _peerDisplay;
    private readonly VerticalStackLayout _envPanel = new() { Spacing = 8 };
    private readonly Label _envHint = new()
    {
        FontSize = 12,
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "正在检查环境…"
    };
    private readonly Label _envStatus = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _uninstallStatus = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly HorizontalStackLayout _repairActions = new() { Spacing = 8, IsVisible = false };
    private Border _dialogShell = null!;
    private Border _titleBar = null!;
    private Border _modeCard = null!;
    private Border _targetCard = null!;
    private Border _optionsCard = null!;
    private Border _pictureCard = null!;
    private Border _featuresCard = null!;
    private VerticalStackLayout _optionsBody = null!;
    private Label _optionsChevron = null!;
    private Label _optionsSummary = null!;
    private CheckBox _audioSyncCheck = null!;
    private Label _audioTitle = null!;
    private CheckBox _remoteControlCheck = null!;
    private Label _remoteControlTitle = null!;
    private CheckBox _cropBlackBarsCheck = null!;
    private Label _cropTitle = null!;
    private Label _optionsHint = null!;
    private Border _envInner = null!;
    private Border _footerBar = null!;
    private Label _titleLabel = null!;
    private Label _envTitleStatus = null!;
    private Label _startProgress = null!;
    private Button _closeBtn = null!;
    private Button _startBtn = null!;
    private Button _repairBtn = null!;
    private Button _recheckBtn = null!;
    private Button _uninstallBtn = null!;
    private Button _cancelBtn = null!;
    private readonly List<ProjectionTarget> _targets = [];
    private readonly IProgress<string> _repairProgress;
    private readonly IProgress<string> _startProgressReport;
    private bool _busy;
    private bool _envReady;
    private int _envSeq;
    private bool _awaitingRepair;
    private bool _optionsExpanded;
    private bool _envTitleCanRepair;

    public ProjectionDialogPage(MauiAppHost host)
    {
        _host = host;
        // 手机/平板：仅复制投影；桌面（含 Maui-Win / MacCatalyst）保留完整选项
        _phoneOnlyClone = DeviceInfo.Platform == DevicePlatform.Android
            || DeviceInfo.Platform == DevicePlatform.iOS;
        Title = "投影";
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromArgb("#80000000");
        _repairProgress = new Progress<string>(s =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _envStatus.Text = s;
                if (_host.IsProjectionEnvironmentRepairing)
                    SetEnvTitleStatus(ready: false, hint: "正在修复…");
            }));
        _startProgressReport = new Progress<string>(s =>
            MainThread.BeginInvokeOnMainThread(() => _startProgress.Text = s ?? ""));

        if (_phoneOnlyClone)
        {
            _modePicker.Items.Add(DisplayProjectionModeInfo.Label(DisplayProjectionMode.Clone));
            _modePicker.SelectedIndex = 0;
            _modePicker.IsEnabled = false;
        }
        else
        {
            foreach (var m in DisplayProjectionModeInfo.PickerModes)
                _modePicker.Items.Add(DisplayProjectionModeInfo.Label(m));
            var prefer = DisplayProjectionModeInfo.ClampForPicker(host.Settings.ScreenExtendProjectionMode);
            _modePicker.SelectedIndex = prefer == DisplayProjectionMode.Clone ? 0 : 1;
        }

        foreach (var s in ProjectionDisplayPresets.ScalePercents)
            _scalePicker.Items.Add($"{s}%");
        _scalePicker.SelectedIndex = 0;
        foreach (var fps in ProjectionDisplayPresets.FrameRates)
            _fpsPicker.Items.Add(ProjectionDisplayPresets.FpsLabel(fps));
        _fpsPicker.SelectedIndex = IndexOfValue(
            ProjectionDisplayPresets.FrameRates, ProjectionDisplayPresets.RememberedFps(host.Settings));
        foreach (var q in ProjectionDisplayPresets.JpegQualities)
            _qualityPicker.Items.Add(ProjectionDisplayPresets.JpegQualityLabel(q));
        _qualityPicker.SelectedIndex = IndexOfValue(
            ProjectionDisplayPresets.JpegQualities, ProjectionDisplayPresets.RememberedJpegQuality(host.Settings));

        _modePicker.SelectedIndexChanged += (_, _) =>
        {
            UpdateHints();
            _ = RefreshDisplayDefaultsAsync();
            if (!_phoneOnlyClone && !_host.IsProjectionEnvironmentRepairing)
                _ = RefreshEnvCheckAsync();
        };
        _targetPicker.SelectedIndexChanged += (_, _) =>
        {
            UpdateHints();
            _ = RefreshDisplayDefaultsAsync();
            if (!_phoneOnlyClone && !_host.IsProjectionEnvironmentRepairing)
                _ = RefreshEnvCheckAsync();
        };
        _resolutionPicker.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressDisplayChange) UpdateDisplayHint();
        };
        _scalePicker.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressDisplayChange) UpdateDisplayHint();
        };
        _fpsPicker.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressDisplayChange) UpdateDisplayHint();
        };
        _qualityPicker.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressDisplayChange) UpdateDisplayHint();
        };

        _startBtn = new Button
        {
            Text = "开始投影",
            CornerRadius = 8,
            HeightRequest = 36,
            FontAttributes = FontAttributes.Bold,
            IsEnabled = false,
            Opacity = 0.5,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumWidthRequest = 100
        };
        _startBtn.Clicked += async (_, _) => await OnStartAsync();

        _repairBtn = new Button
        {
            Text = "自动修复环境",
            CornerRadius = 8,
            HeightRequest = 36,
            FontAttributes = FontAttributes.Bold
        };
        _repairBtn.Clicked += async (_, _) => await OnRepairAsync();

        _recheckBtn = new Button
        {
            Text = "重新检测",
            BackgroundColor = Colors.Transparent,
            HeightRequest = 36
        };
        _recheckBtn.Clicked += async (_, _) => await RefreshEnvCheckAsync();
        _repairActions.Children.Add(_repairBtn);
        _repairActions.Children.Add(_recheckBtn);

        _envPanel.Children.Add(new Label { Text = "环境", FontAttributes = FontAttributes.Bold });
        _envPanel.Children.Add(_envHint);
        _envPanel.Children.Add(_repairActions);
        _envPanel.Children.Add(_envStatus);

        _uninstallBtn = new Button
        {
            Text = "卸载并清理",
            BackgroundColor = Colors.Transparent,
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.Start,
            IsVisible = false,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _uninstallBtn.Clicked += async (_, _) => await OnUninstallAsync();
        // 合并进环境卡片：通过/已装时显示
        _envPanel.Children.Add(_uninstallBtn);
        _envPanel.Children.Add(_uninstallStatus);

        _cancelBtn = new Button
        {
            Text = "取消",
            BackgroundColor = Colors.Transparent,
            HeightRequest = 36,
            MinimumWidthRequest = 88
        };
        _cancelBtn.Clicked += async (_, _) => await CloseAsync();

        // 与主窗标题栏对齐：高 36、标题 12、关闭 46×36 / 悬停 #E81123
        _titleLabel = new Label
        {
            Text = "投影",
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        _envTitleStatus = new Label
        {
            Text = "正在检查…",
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
            HorizontalTextAlignment = TextAlignment.End,
            LineBreakMode = LineBreakMode.TailTruncation,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var envTitleTap = new TapGestureRecognizer();
        envTitleTap.Tapped += async (_, _) =>
        {
            if (!_envTitleCanRepair || _busy || _host.IsProjectionEnvironmentRepairing) return;
            if (!_optionsExpanded)
                SetOptionsExpanded(true);
            await OnRepairAsync();
        };
        _envTitleStatus.GestureRecognizers.Add(envTitleTap);
        _closeBtn = new Button
        {
            Text = "✕",
            FontSize = 11,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 46,
            HeightRequest = 36,
            MinimumWidthRequest = 46,
            MinimumHeightRequest = 36,
            Padding = 0,
            Margin = 0,
            CornerRadius = 0,
            BorderWidth = 0,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Fill
        };
        _closeBtn.Clicked += async (_, _) => await CloseAsync();
        ApplyCloseButtonVisualStates(_closeBtn);

        _titleBar = new Border
        {
            StrokeThickness = 0,
            HeightRequest = 36,
            MinimumHeightRequest = 36,
            MaximumHeightRequest = 36,
            Padding = new Thickness(12, 0, 0, 0),
            Content = new Grid
            {
                HeightRequest = 36,
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star),
                    new(GridLength.Auto),
                    new(GridLength.Auto)
                },
                Children =
                {
                    _titleLabel,
                    _envTitleStatus,
                    _closeBtn
                }
            }
        };
        Microsoft.Maui.Controls.Grid.SetColumn(_titleLabel, 0);
        Microsoft.Maui.Controls.Grid.SetColumn(_envTitleStatus, 2);
        Microsoft.Maui.Controls.Grid.SetColumn(_closeBtn, 3);

        _modeCard = MakeCard("投影方式", _modePicker, _modeHint);
        _targetCard = MakeCard("目标", _targetPicker, _targetHint);
        var pictureGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(12)),
                new ColumnDefinition(GridLength.Star)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(10)),
                new RowDefinition(GridLength.Auto)
            }
        };
        pictureGrid.Add(Field("分辨率", _resolutionPicker), 0, 0);
        pictureGrid.Add(Field("缩放", _scalePicker), 2, 0);
        pictureGrid.Add(Field("帧率", _fpsPicker), 0, 2);
        pictureGrid.Add(Field("画质", _qualityPicker), 2, 2);

        _audioSyncCheck = new CheckBox
        {
            IsChecked = host.Settings.ScreenExtendAudioSync,
            VerticalOptions = LayoutOptions.Center
        };
        _audioSyncCheck.CheckedChanged += (_, _) => UpdateOptionsSummary();
        _audioTitle = new Label
        {
            Text = "声音同步",
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        _remoteControlCheck = new CheckBox
        {
            IsChecked = host.Settings.ScreenExtendRemoteControl,
            VerticalOptions = LayoutOptions.Center
        };
        _remoteControlCheck.CheckedChanged += (_, _) => UpdateOptionsSummary();
        _remoteControlTitle = new Label
        {
            Text = "远程控制",
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        _cropBlackBarsCheck = new CheckBox
        {
            IsChecked = host.Settings.ScreenExtendCropBlackBars,
            VerticalOptions = LayoutOptions.Center
        };
        _cropBlackBarsCheck.CheckedChanged += (_, _) => UpdateOptionsSummary();
        _cropTitle = new Label
        {
            Text = "去黑边",
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        _optionsHint = new Label
        {
            Text = "默认关闭。声音同步：目标播本地音；远程控制：全屏播放器内回控；去黑边：裁四周黑边。",
            FontSize = 11,
            LineBreakMode = LineBreakMode.WordWrap
        };
        _displayHint.FontSize = 11;
        var optionsGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        optionsGrid.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { _audioSyncCheck, _audioTitle }
        }, 0, 0);
        optionsGrid.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { _remoteControlCheck, _remoteControlTitle }
        }, 1, 0);
        optionsGrid.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { _cropBlackBarsCheck, _cropTitle }
        }, 2, 0);

        _envInner = new Border
        {
            StrokeThickness = 1,
            Padding = new Thickness(12, 10),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = _envPanel
        };

        _optionsChevron = new Label
        {
            Text = "▸",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        _optionsSummary = new Label
        {
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var optionsTitle = new Label
        {
            Text = "选项",
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var optionsHeader = new Grid
        {
            Padding = new Thickness(0, 2),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Children = { optionsTitle, _optionsSummary, _optionsChevron }
        };
        Microsoft.Maui.Controls.Grid.SetColumn(optionsTitle, 0);
        Microsoft.Maui.Controls.Grid.SetColumn(_optionsSummary, 1);
        Microsoft.Maui.Controls.Grid.SetColumn(_optionsChevron, 2);
        var headerTap = new TapGestureRecognizer();
        headerTap.Tapped += (_, _) => SetOptionsExpanded(!_optionsExpanded);
        optionsHeader.GestureRecognizers.Add(headerTap);

        _pictureCard = WrapInnerCard(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "画面", FontAttributes = FontAttributes.Bold },
                pictureGrid,
                _displayHint
            }
        });
        _featuresCard = WrapInnerCard(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "功能", FontAttributes = FontAttributes.Bold },
                optionsGrid,
                _optionsHint
            }
        });
        _optionsBody = new VerticalStackLayout
        {
            Spacing = 10,
            IsVisible = false,
            Children = { _pictureCard, _featuresCard }
        };
        if (!_phoneOnlyClone)
            _optionsBody.Children.Add(_envInner);

        _optionsCard = new Border
        {
            StrokeThickness = 1,
            Padding = new Thickness(14, 12),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children = { optionsHeader, _optionsBody }
            }
        };

        _startProgress = new Label
        {
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, 0, 12, 0)
        };
        _footerBar = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(16, 12),
            Content = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Children =
                {
                    _startProgress,
                    new HorizontalStackLayout
                    {
                        Spacing = 10,
                        HorizontalOptions = LayoutOptions.End,
                        Children = { _cancelBtn, _startBtn }
                    }
                }
            }
        };
        var footerActions = (HorizontalStackLayout)((Grid)_footerBar.Content!).Children[1];
        Microsoft.Maui.Controls.Grid.SetColumn(_startProgress, 0);
        Microsoft.Maui.Controls.Grid.SetColumn(footerActions, 1);

        var bodyStack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 8, 16, 8),
            Spacing = 12,
            Children = { _modeCard, _targetCard, _optionsCard }
        };

        var body = new ScrollView
        {
            VerticalOptions = LayoutOptions.Fill,
            Content = bodyStack
        };

        // 理想尺寸 520×480；手机竖屏/横屏按视口收缩，避免宽高溢出（中间区仍可滚动）
        _dialogShell = new Border
        {
            StrokeThickness = 1,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Padding = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
                Children = { _titleBar, body, _footerBar }
            }
        };
        Microsoft.Maui.Controls.Grid.SetRow(_titleBar, 0);
        Microsoft.Maui.Controls.Grid.SetRow(body, 1);
        Microsoft.Maui.Controls.Grid.SetRow(_footerBar, 2);

        var root = new Grid
        {
            Padding = new Thickness(24),
            Children = { _dialogShell }
        };
        Content = root;
        SizeChanged += (_, _) => ApplyDialogShellSize();
        ApplyDialogShellSize();

        ApplyTheme();
        Appearing += OnAppearing;
        Disappearing += OnDisappearing;
    }

    /// <summary>按当前页尺寸限制弹窗外壳，竖屏不超宽、横屏不超高。</summary>
    private void ApplyDialogShellSize()
    {
        const double idealW = 520;
        const double idealH = 480;
        const double minW = 280;
        const double minH = 240;

        var pageW = Width > 1 ? Width : EstimatePageWidth();
        var pageH = Height > 1 ? Height : EstimatePageHeight();
        // 窄屏缩小外边距，给弹窗多留可用宽高
        var pad = pageW < 420 || pageH < 420 ? 12d : 24d;
        if (Content is Grid root)
            root.Padding = new Thickness(pad);

        var maxW = Math.Max(minW, pageW - pad * 2);
        var maxH = Math.Max(minH, pageH - pad * 2);
        var shellW = Math.Min(idealW, maxW);
        var shellH = Math.Min(idealH, maxH);

        _dialogShell.WidthRequest = shellW;
        _dialogShell.MaximumWidthRequest = shellW;
        _dialogShell.MinimumWidthRequest = Math.Min(320, shellW);
        _dialogShell.HeightRequest = shellH;
        _dialogShell.MaximumHeightRequest = shellH;
        _dialogShell.MinimumHeightRequest = shellH;

        // 窄宽时降低按钮最小宽，避免底栏「取消 / 开始投影」挤出
        if (_cancelBtn != null)
            _cancelBtn.MinimumWidthRequest = shellW < 360 ? 64 : 88;
        if (_startBtn != null)
            _startBtn.MinimumWidthRequest = shellW < 360 ? 72 : 100;
    }

    private static double EstimatePageWidth()
    {
        try
        {
            var d = DeviceDisplay.MainDisplayInfo;
            if (d.Width > 0 && d.Density > 0)
                return d.Width / d.Density;
        }
        catch { /* ignore */ }
        return idealFallbackW;
    }

    private static double EstimatePageHeight()
    {
        try
        {
            var d = DeviceDisplay.MainDisplayInfo;
            if (d.Height > 0 && d.Density > 0)
                return d.Height / d.Density;
        }
        catch { /* ignore */ }
        return idealFallbackH;
    }

    private const double idealFallbackW = 520;
    private const double idealFallbackH = 480;

    private void OnAppearing(object? sender, EventArgs e)
    {
        ApplyTheme();
        ApplyDialogShellSize();
#if WINDOWS
        Platforms.Windows.MauiWindowChrome.RefreshAfterModal();
#endif
        RefreshTargets();
        UpdateHints();
        _ = RefreshDisplayDefaultsAsync();
        if (_phoneOnlyClone)
        {
            _envReady = true;
            SetEnvTitleStatus(ready: true);
            UpdateStartEnabled();
            return;
        }

        RefreshUninstallPanel();
#if WINDOWS
        MttVddRepairSession.AttachProgress(_repairProgress);
        MttVddRepairSession.Changed += OnRepairSessionChanged;
#endif
        _ = BootstrapEnvAsync();
    }

    private void OnDisappearing(object? sender, EventArgs e)
    {
#if WINDOWS
        MttVddRepairSession.Changed -= OnRepairSessionChanged;
        MttVddRepairSession.DetachProgress(_repairProgress);
        // 关闭 Modal 后再刷一次主窗标题栏
        Platforms.Windows.MauiWindowChrome.RefreshAfterModal();
#endif
        // 不取消修复：后台继续安装
    }

    private void OnRepairSessionChanged()
        => MainThread.BeginInvokeOnMainThread(UpdateRepairActionButtons);

    private async Task BootstrapEnvAsync()
    {
        if (_host.IsProjectionEnvironmentRepairing)
        {
            _repairActions.IsVisible = true;
            _envHint.Text = "正在自动修复虚拟显示驱动环境，请勿重复操作…";
            _envStatus.Text = string.IsNullOrWhiteSpace(_host.ProjectionEnvironmentRepairStatus)
                ? "正在修复…"
                : _host.ProjectionEnvironmentRepairStatus;
            _envReady = false;
            UpdateRepairActionButtons();
            await AwaitRepairAsync(startIfNeeded: false);
            return;
        }

        await RefreshEnvCheckAsync();
    }

    private Task CloseAsync() => Navigation.PopModalAsync();

    private void ApplyTheme()
    {
        MauiThemeService.Apply(MauiThemeService.Current, this);
        var text = ColorOf("Text") ?? Color.FromArgb("#111827");
        var muted = ColorOf("Muted") ?? Color.FromArgb("#6B7280");
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var subtle = ColorOf("SubtleBg") ?? Color.FromArgb("#F3F4F6");
        var winBg = ColorOf("WinBg") ?? Color.FromArgb("#F7F8FA");
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");

        // 遮罩保持半透明，不跟主题 WinBg
        BackgroundColor = Color.FromArgb("#80000000");
        StylePicker(_modePicker, text, card);
        StylePicker(_targetPicker, text, card);
        StylePicker(_resolutionPicker, text, card);
        StylePicker(_scalePicker, text, card);
        StylePicker(_fpsPicker, text, card);
        StylePicker(_qualityPicker, text, card);
        foreach (var caption in _fieldCaptions)
            caption.TextColor = muted;
        _modeHint.TextColor = muted;
        _targetHint.TextColor = muted;
        _displayHint.TextColor = muted;
        _optionsHint.TextColor = muted;
        _envHint.TextColor = muted;
        _envStatus.TextColor = text;
        _uninstallStatus.TextColor = text;
        _titleLabel.TextColor = text;
        _optionsChevron.TextColor = muted;
        _optionsSummary.TextColor = muted;
        _startProgress.TextColor = muted;
        // 关闭钮默认跟标题字色；悬停由 VisualState 刷成白底红（与主窗一致）
        if (_closeBtn.BackgroundColor == Colors.Transparent
            || _closeBtn.BackgroundColor == Colors.White)
            _closeBtn.TextColor = text;
        _remoteControlTitle.TextColor = text;

        _dialogShell.BackgroundColor = winBg;
        _dialogShell.Stroke = line;
        _titleBar.BackgroundColor = winBg;
        _footerBar.BackgroundColor = winBg;

        PaintCard(_modeCard, card, line, text);
        PaintCard(_targetCard, card, line, text);
        PaintCard(_optionsCard, card, line, text);
        PaintCard(_pictureCard, subtle, line, text);
        PaintCard(_featuresCard, subtle, line, text);
        _audioTitle.TextColor = text;
        _cropTitle.TextColor = text;
        _envInner.BackgroundColor = subtle;
        _envInner.Stroke = line;
        PaintPanelLabels(_envPanel, text);

        _startBtn.BackgroundColor = blue;
        _startBtn.TextColor = Colors.White;
        _repairBtn.BackgroundColor = blue;
        _repairBtn.TextColor = Colors.White;
        _recheckBtn.TextColor = text;
        _cancelBtn.TextColor = text;
        _cancelBtn.BorderColor = line;
        _cancelBtn.BorderWidth = 1;
        _uninstallBtn.TextColor = Color.FromArgb("#B91C1C");
        UpdateRepairActionButtons();
    }

    private static void StylePicker(Picker p, Color text, Color bg)
    {
        p.TextColor = text;
        p.BackgroundColor = bg;
        p.TitleColor = text;
        p.HeightRequest = 40;
    }

    private static void PaintCard(Border border, Color bg, Color line, Color text)
    {
        border.BackgroundColor = bg;
        border.Stroke = line;
        if (border.Content is VerticalStackLayout stack)
        {
            foreach (var c in stack.Children)
            {
                if (c is Label lab && lab.FontAttributes == FontAttributes.Bold)
                    lab.TextColor = text;
            }
        }
    }

    private static void PaintPanelLabels(VerticalStackLayout panel, Color text)
    {
        foreach (var c in panel.Children)
        {
            if (c is Label lab && lab.FontAttributes == FontAttributes.Bold)
                lab.TextColor = text;
        }
    }

    private static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }

    private static Border MakeCard(string title, View body, Label hint) => new()
    {
        StrokeThickness = 1,
        Padding = new Thickness(14, 12),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        Content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = title, FontAttributes = FontAttributes.Bold },
                body,
                hint
            }
        }
    };

    private static Border WrapCard(View inner) => new()
    {
        StrokeThickness = 1,
        Padding = new Thickness(14, 12),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        Content = inner
    };

    private static Border WrapInnerCard(View inner) => new()
    {
        StrokeThickness = 1,
        Padding = new Thickness(12, 10),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
        Content = inner
    };

    /// <summary>对齐主窗 TitleBarCloseBtn：常态透明，悬停 #E81123 + 白字。</summary>
    private static void ApplyCloseButtonVisualStates(Button btn)
    {
        var groups = new VisualStateGroupList();
        var common = new VisualStateGroup { Name = "CommonStates" };
        common.States.Add(new VisualState
        {
            Name = "Normal",
            Setters =
            {
                new Setter { Property = Button.BackgroundColorProperty, Value = Colors.Transparent }
            }
        });
        common.States.Add(new VisualState
        {
            Name = "PointerOver",
            Setters =
            {
                new Setter { Property = Button.BackgroundColorProperty, Value = Color.FromArgb("#E81123") },
                new Setter { Property = Button.TextColorProperty, Value = Colors.White }
            }
        });
        common.States.Add(new VisualState
        {
            Name = "Pressed",
            Setters =
            {
                new Setter { Property = Button.BackgroundColorProperty, Value = Color.FromArgb("#C50F1F") },
                new Setter { Property = Button.TextColorProperty, Value = Colors.White }
            }
        });
        groups.Add(common);
        VisualStateManager.SetVisualStateGroups(btn, groups);
    }

    private void RefreshUninstallPanel()
        => _ = RefreshUninstallPanelAsync();

    private async Task RefreshUninstallPanelAsync()
    {
#if WINDOWS
        var show = false;
        if (!_host.IsProjectionEnvironmentRepairing)
        {
            // 环境检查通过直接显示；不通过时若已装 VDD/控制台也显示
            var installed = await Task.Run(() =>
                MttVddEnvironment.IsProductInstalled()
                || MttVddInstaller.HasAnyControlExecutable()
                || MttVddInstaller.HasInstalledControlExecutable());
            show = _envReady || installed;
        }

        _uninstallBtn.IsVisible = show;
        if (!show)
            _uninstallStatus.Text = "";
#else
        _uninstallBtn.IsVisible = false;
        await Task.CompletedTask;
#endif
    }

    private void SetOptionsExpanded(bool expanded)
    {
        _optionsExpanded = expanded;
        _optionsBody.IsVisible = expanded;
        _optionsChevron.Text = expanded ? "▾" : "▸";
        _optionsSummary.IsVisible = !expanded;
        if (!expanded)
            UpdateOptionsSummary();
    }

    private void UpdateOptionsSummary()
    {
        if (_optionsSummary == null) return;
        try
        {
            var c = ReadDisplayChoice();
            var parts = new List<string>
            {
                c.ResolutionLabel,
                c.ScaleLabel,
                $"{c.Fps} 帧",
                ProjectionDisplayPresets.JpegQualityLabel(c.JpegQuality)
            };
            if (_audioSyncCheck.IsChecked == true) parts.Add("声音同步");
            if (_remoteControlCheck.IsChecked == true) parts.Add("远程控制");
            if (_cropBlackBarsCheck.IsChecked == true) parts.Add("去黑边");
            _optionsSummary.Text = string.Join(" · ", parts);
        }
        catch
        {
            _optionsSummary.Text = "";
        }
    }

    /// <summary>标题栏只显示短态；详情留在选项内「环境」区。异常可点进入修复。</summary>
    private void SetEnvTitleStatus(bool ready, string? hint = null, bool showRepair = false)
    {
        string text;
        if (_host.IsProjectionEnvironmentRepairing)
            text = "正在修复…";
        else if ((hint ?? "").Trim().StartsWith("正在检查", StringComparison.Ordinal))
            text = "正在检查…";
        else
            text = ready ? "环境正常" : "环境异常";

        _envTitleStatus.Text = text;
        _envTitleCanRepair = showRepair && !_host.IsProjectionEnvironmentRepairing && text == "环境异常";
        _envTitleStatus.TextColor = ready && !_host.IsProjectionEnvironmentRepairing
            ? Color.FromArgb("#059669")
            : text is "正在检查…" or "正在修复…"
                ? (ColorOf("Muted") ?? Color.FromArgb("#6B7280"))
                : Color.FromArgb("#B91C1C");
    }

    private void RefreshTargets()
    {
        _targets.Clear();
        _targetPicker.Items.Clear();
#if WINDOWS
        var locals = MttVddEnvironment.ListLocalSecondaryLabels();
#else
        IReadOnlyList<(string Id, string Label)> locals = Array.Empty<(string, string)>();
#endif
        foreach (var t in ProjectionTargets.ListForPicker(_host.Connections, locals, _host.Settings.ScreenExtendPeer))
        {
            _targets.Add(t);
            _targetPicker.Items.Add(t.DisplayName);
        }

        var prefer = _host.Settings.ScreenExtendPeer;
        if (!string.IsNullOrWhiteSpace(prefer))
        {
            for (var i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].Kind == ProjectionTargetKind.Peer
                    && string.Equals(_targets[i].Id, prefer, StringComparison.OrdinalIgnoreCase))
                {
                    _targetPicker.SelectedIndex = i;
                    return;
                }
            }
        }

        if (_targets.Count > 0)
            _targetPicker.SelectedIndex = 0;
    }

    private DisplayProjectionMode ReadMode()
    {
        if (_phoneOnlyClone) return DisplayProjectionMode.Clone;
        return _modePicker.SelectedIndex == 0 ? DisplayProjectionMode.Clone : DisplayProjectionMode.Extend;
    }

    private ProjectionTarget? ReadTarget()
    {
        var i = _targetPicker.SelectedIndex;
        return i >= 0 && i < _targets.Count ? _targets[i] : null;
    }

    private async Task RefreshDisplayDefaultsAsync()
    {
        var seq = ++_displaySeq;
        var mode = ReadMode();
        var target = ReadTarget();
        var local = _host.TryGetLocalDisplayInfo()
                    ?? new DisplayInfoPayload { Width = 1920, Height = 1080, ScalePercent = 100, RefreshHz = 60 };
        _peerDisplay = null;

        if (!_phoneOnlyClone
            && mode == DisplayProjectionMode.Extend
            && target?.Kind == ProjectionTargetKind.Peer
            && _host.Connections.GetPeerStatus(target.Id).Bidirectional)
        {
            var peerInfo = await _host.QueryPeerDisplayAsync(target.Id);
            if (seq != _displaySeq) return;
            _peerDisplay = peerInfo;
        }

        if (seq != _displaySeq) return;
        ApplyDisplayPickers(mode, local, _peerDisplay);
        UpdateDisplayHint();
    }

    private void ApplyDisplayPickers(DisplayProjectionMode mode, DisplayInfoPayload local, DisplayInfoPayload? peer)
    {
        _suppressDisplayChange = true;
        try
        {
            (ProjectionDisplaySource Source, int W, int H, int Hz)? previous = null;
            if (_displaySelectionReady
                && _resolutionPicker.SelectedIndex >= 0
                && _resolutionPicker.SelectedIndex < _resOptions.Count)
                previous = _resOptions[_resolutionPicker.SelectedIndex];

            _resOptions.Clear();
            _resolutionPicker.Items.Clear();
            _resOptions.Add((ProjectionDisplaySource.HostPrimary, local.Width, local.Height, local.RefreshHz));
            _resolutionPicker.Items.Add($"本机主屏 {local.Width}×{local.Height}");

            if (peer is { Width: > 0, Height: > 0 })
            {
                _resOptions.Add((ProjectionDisplaySource.TargetPeer, peer.Width, peer.Height, peer.RefreshHz));
                _resolutionPicker.Items.Add($"目标电脑 {peer.Width}×{peer.Height}");
            }
            else if (mode == DisplayProjectionMode.Extend && !_phoneOnlyClone)
            {
                _resOptions.Add((ProjectionDisplaySource.TargetPeer, local.Width, local.Height, local.RefreshHz));
                _resolutionPicker.Items.Add("目标电脑（连接后自动）");
            }

            foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
            {
                if ((w == local.Width && h == local.Height)
                    || (peer != null && w == peer.Width && h == peer.Height))
                    continue;
                _resOptions.Add((ProjectionDisplaySource.Custom, w, h, 60));
                _resolutionPicker.Items.Add($"{w}×{h}");
            }

            if (!SelectRememberedResolution(previous))
            {
                _resolutionPicker.SelectedIndex = mode == DisplayProjectionMode.Clone ? 0
                    : (_resOptions.Count > 1 ? 1 : 0);
            }

            var scale = _host.Settings.ScreenExtendScalePercent > 0
                ? _host.Settings.ScreenExtendScalePercent
                : mode == DisplayProjectionMode.Clone
                    ? local.ScalePercent
                    : (peer?.ScalePercent > 0 ? peer.ScalePercent : local.ScalePercent);
            if (_displaySelectionReady && _scalePicker.SelectedIndex >= 0
                && _scalePicker.SelectedIndex < ProjectionDisplayPresets.ScalePercents.Count)
                scale = ProjectionDisplayPresets.ScalePercents[_scalePicker.SelectedIndex];
            scale = ProjectionDisplayPresets.ClampScale(scale);
            _displaySelectionReady = true;
            var si = ProjectionDisplayPresets.ScalePercents.ToList().IndexOf(scale);
            _scalePicker.SelectedIndex = si >= 0 ? si : 0;
        }
        finally
        {
            _suppressDisplayChange = false;
        }
    }

    private ProjectionDisplayChoice ReadDisplayChoice()
    {
        var i = _resolutionPicker.SelectedIndex;
        var scaleIdx = _scalePicker.SelectedIndex;
        var scale = scaleIdx >= 0 && scaleIdx < ProjectionDisplayPresets.ScalePercents.Count
            ? ProjectionDisplayPresets.ScalePercents[scaleIdx]
            : 100;
        if (i < 0 || i >= _resOptions.Count)
        {
            var local = _host.TryGetLocalDisplayInfo();
            return new ProjectionDisplayChoice
            {
                Source = ProjectionDisplaySource.HostPrimary,
                Width = local?.Width ?? 1920,
                Height = local?.Height ?? 1080,
                ScalePercent = scale,
                RefreshHz = local?.RefreshHz ?? 60,
                CropBlackBars = _cropBlackBarsCheck.IsChecked == true,
                Fps = ReadPickerValue(ProjectionDisplayPresets.FrameRates, _fpsPicker.SelectedIndex, ProjectionDisplayPresets.DefaultFps),
                JpegQuality = ReadPickerValue(ProjectionDisplayPresets.JpegQualities, _qualityPicker.SelectedIndex, ProjectionDisplayPresets.DefaultJpegQuality)
            };
        }

        var opt = _resOptions[i];
        return new ProjectionDisplayChoice
        {
            Source = opt.Source,
            Width = opt.W,
            Height = opt.H,
            ScalePercent = scale,
            RefreshHz = opt.Hz > 0 ? opt.Hz : 60,
            CropBlackBars = _cropBlackBarsCheck.IsChecked == true,
            Fps = ReadPickerValue(ProjectionDisplayPresets.FrameRates, _fpsPicker.SelectedIndex, ProjectionDisplayPresets.DefaultFps),
            JpegQuality = ReadPickerValue(ProjectionDisplayPresets.JpegQualities, _qualityPicker.SelectedIndex, ProjectionDisplayPresets.DefaultJpegQuality)
        };
    }

    private static int ReadPickerValue(IReadOnlyList<int> values, int index, int fallback)
        => index >= 0 && index < values.Count ? values[index] : fallback;

    private static int IndexOfValue(IReadOnlyList<int> values, int value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] == value) return i;
        }

        return 0;
    }

    private bool SelectRememberedResolution(
        (ProjectionDisplaySource Source, int W, int H, int Hz)? previous)
    {
        if (_displaySelectionReady && previous != null)
            return SelectResolution(previous.Value.Source, previous.Value.W, previous.Value.H);

        if (_displaySelectionReady)
            return false;

        var settings = _host.Settings;
        return settings.ScreenExtendWidth > 0 && settings.ScreenExtendHeight > 0
            && SelectResolution(
                (ProjectionDisplaySource)settings.ScreenExtendDisplaySource,
                settings.ScreenExtendWidth,
                settings.ScreenExtendHeight);
    }

    private bool SelectResolution(ProjectionDisplaySource source, int width, int height)
    {
        var sizeMatch = -1;
        for (var i = 0; i < _resOptions.Count; i++)
        {
            var opt = _resOptions[i];
            if (opt.W != width || opt.H != height) continue;
            if (opt.Source == source)
            {
                _resolutionPicker.SelectedIndex = i;
                return true;
            }

            if (sizeMatch < 0)
                sizeMatch = i;
        }

        if (sizeMatch < 0) return false;
        _resolutionPicker.SelectedIndex = sizeMatch;
        return true;
    }

    private void RememberChoices()
    {
        if (_resolutionPicker.SelectedIndex < 0 || _fpsPicker.SelectedIndex < 0)
            return;
        var choice = ReadDisplayChoice();
        ProjectionDisplayPresets.WriteRemembered(
            _host.Settings, choice,
            _audioSyncCheck.IsChecked == true,
            _remoteControlCheck.IsChecked == true);
        _host.Settings.Save();
    }

    protected override void OnDisappearing()
    {
        RememberChoices();
        base.OnDisappearing();
    }

    private View Field(string caption, View input)
    {
        var label = new Label { Text = caption, FontSize = 12 };
        _fieldCaptions.Add(label);
        return new VerticalStackLayout
        {
            Spacing = 4,
            Children = { label, input }
        };
    }

    private void UpdateDisplayHint()
    {
        var mode = ReadMode();
        var c = ReadDisplayChoice();
        var rate = $"{c.Fps} 帧 · {ProjectionDisplayPresets.JpegQualityLabel(c.JpegQuality)}";
        if (mode == DisplayProjectionMode.Clone)
        {
            _displayHint.Text = _phoneOnlyClone
                ? $"复制本机画面。{c.ResolutionLabel} · {c.ScaleLabel} · {rate}。"
                : $"复制本机主屏。{c.ResolutionLabel} · {c.ScaleLabel} · {rate}。";
        }
        else
            _displayHint.Text = $"扩展副屏 {c.ResolutionLabel} · {c.ScaleLabel} · {rate}，主屏不变。";
        UpdateOptionsSummary();
    }

    private void UpdateHints()
    {
        var mode = ReadMode();
        _modeHint.Text = _phoneOnlyClone
            ? "手机端仅支持复制投影"
            : DisplayProjectionModeInfo.Description(mode);
        var t = ReadTarget();
        if (t == null)
            _targetHint.Text = "暂无目标。请连接电脑或接入副屏。";
        else if (t.Kind == ProjectionTargetKind.LocalSecondaryMonitor)
            _targetHint.Text = "采集本机副屏，并推送到一台双向已连接电脑。";
        else if (mode == DisplayProjectionMode.Extend)
            _targetHint.Text = "扩展到该电脑：需真副屏或虚拟显示驱动。";
        else
            _targetHint.Text = "复制主屏到该电脑（无需虚拟驱动）。";
        UpdateDisplayHint();
    }

    private void UpdateStartEnabled()
    {
        var hasTarget = ReadTarget() != null;
        if (_phoneOnlyClone)
        {
            _startBtn.IsEnabled = !_busy && hasTarget;
        }
        else
        {
            _startBtn.IsEnabled = !_busy && _envReady && hasTarget && !_host.IsProjectionEnvironmentRepairing;
        }

        _startBtn.Opacity = _startBtn.IsEnabled ? 1 : 0.5;
    }

    private void UpdateRepairActionButtons()
    {
        var repairing = _host.IsProjectionEnvironmentRepairing || _awaitingRepair;
        _repairBtn.IsEnabled = !repairing && !_busy;
        _recheckBtn.IsEnabled = !repairing && !_busy;
        _uninstallBtn.IsEnabled = !repairing && !_busy;
        _repairBtn.Opacity = _repairBtn.IsEnabled ? 1 : 0.5;
        _recheckBtn.Opacity = _recheckBtn.IsEnabled ? 1 : 0.5;
        UpdateStartEnabled();
    }

    private void SetEnvUi(string hint, bool ready, bool showRepair, string? status = null)
    {
        _envReady = ready;
        _envHint.Text = hint;
        if (status != null || !_host.IsProjectionEnvironmentRepairing)
            _envStatus.Text = status ?? "";
        var show = showRepair || _host.IsProjectionEnvironmentRepairing;
        _repairActions.IsVisible = show;
        SetEnvTitleStatus(ready, hint, show);
        if (show && !_optionsExpanded)
            SetOptionsExpanded(true);
        UpdateRepairActionButtons();
        RefreshUninstallPanel();
    }

    private async Task RefreshEnvCheckAsync()
    {
        if (_host.IsProjectionEnvironmentRepairing)
        {
            _repairActions.IsVisible = true;
            var s = string.IsNullOrWhiteSpace(_host.ProjectionEnvironmentRepairStatus)
                ? "正在修复…"
                : _host.ProjectionEnvironmentRepairStatus;
            _envStatus.Text = s;
            SetEnvTitleStatus(ready: false, hint: "正在修复…");
            if (!_optionsExpanded)
                SetOptionsExpanded(true);
            UpdateRepairActionButtons();
            return;
        }

        var seq = ++_envSeq;
        SetEnvUi("正在检查环境…", ready: false, showRepair: false);

        var mode = ReadMode();
        var t = ReadTarget();

        if (t == null)
        {
            if (seq != _envSeq) return;
            SetEnvUi("请选择投影目标（本机副屏或双向已连接电脑）。", false, false);
            return;
        }

        if (t.Kind == ProjectionTargetKind.Peer
            && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
        {
            if (seq != _envSeq) return;
            SetEnvUi($"「{t.DisplayName}」尚未双向连接，无法投影。", false, false);
            return;
        }

        if (mode == DisplayProjectionMode.Clone)
        {
            if (seq != _envSeq) return;
            SetEnvUi("复制模式镜像主屏，无需虚拟显示驱动。环境检查已通过。", true, false);
            return;
        }

#if WINDOWS
        if (t.Kind == ProjectionTargetKind.LocalSecondaryMonitor)
        {
            var hasPeer = ScreenExtendTargets.ListEligible(_host.Connections).Count > 0
                          || (!string.IsNullOrWhiteSpace(_host.Settings.ScreenExtendPeer)
                              && _host.Connections.GetPeerStatus(_host.Settings.ScreenExtendPeer).Bidirectional);
            if (seq != _envSeq) return;
            if (!hasPeer)
            {
                SetEnvUi("已选本机副屏，但仍需至少一台双向已连接电脑作为接收端。", false, false);
                return;
            }

            SetEnvUi("本机副屏可用，扩展采集环境检查已通过。", true, false);
            return;
        }

        var (ready, detail) = await MttVddEnvironment.CheckExtendReadyAsync();
        if (seq != _envSeq) return;
        if (ready)
        {
            SetEnvUi(detail + "。环境检查已通过。", true, false);
            return;
        }

        SetEnvUi(
            detail + "。可自动安装（离线 VddOffline 优先，其次 winget）。",
            false,
            showRepair: true);
#else
        if (seq != _envSeq) return;
        SetEnvUi("当前平台不支持发起投影；请在 Windows 主机操作。", false, false);
        await Task.CompletedTask;
#endif
    }

    private async Task AwaitRepairAsync(bool startIfNeeded)
    {
        if (_awaitingRepair) return;
        _awaitingRepair = true;
        UpdateRepairActionButtons();
        try
        {
            if (startIfNeeded)
                _envStatus.Text = "正在修复…";
            var (_, msg) = await _host.RepairProjectionEnvironmentAsync(_repairProgress);
            _envStatus.Text = msg;
            RefreshUninstallPanel();
            await RefreshEnvCheckAsync();
        }
        finally
        {
            _awaitingRepair = false;
            UpdateRepairActionButtons();
            RefreshUninstallPanel();
        }
    }

    private async Task OnStartAsync()
    {
        if (_busy || !_envReady || _host.IsProjectionEnvironmentRepairing) return;
        _busy = true;
        _startProgress.Text = "";
        UpdateRepairActionButtons();
        try
        {
            var result = await _host.StartProjectionAsync(
                ReadTarget(), ReadMode(), ReadDisplayChoice(),
                _audioSyncCheck.IsChecked == true,
                _remoteControlCheck.IsChecked == true,
                _startProgressReport);
            if (result.NeedEnvironmentRepair)
            {
                _startProgress.Text = "失败：需要修复环境";
                SetEnvUi(result.Message ?? "需要安装虚拟显示驱动", false, true, "请点击「自动修复环境」");
                return;
            }

            if (!result.Ok)
            {
                _startProgress.Text = "失败：" + (result.Message ?? "投影失败");
                await DisplayAlertAsync("投影", result.Message ?? "投影失败", "确定");
                return;
            }

#if WINDOWS
            Platforms.Windows.MauiProjectionTitleBar.SetProjecting(true);
#endif
            await Navigation.PopModalAsync();
        }
        finally
        {
            _busy = false;
            UpdateRepairActionButtons();
        }
    }

    private async Task OnRepairAsync()
    {
#if WINDOWS
        if (_busy || _host.IsProjectionEnvironmentRepairing || _awaitingRepair) return;
        _repairActions.IsVisible = true;
        await AwaitRepairAsync(startIfNeeded: true);
#else
        await DisplayAlertAsync("投影", "仅 Windows 主机支持环境修复", "确定");
#endif
    }

    private async Task OnUninstallAsync()
    {
#if WINDOWS
        if (_busy || _host.IsProjectionEnvironmentRepairing) return;
        var confirm = await DisplayAlertAsync(
            "卸载虚拟显示驱动",
            "将卸载本程序安装的 Virtual Display Driver，并尽量清理相关程序与数据。是否继续？",
            "卸载",
            "取消");
        if (!confirm) return;

        _busy = true;
        UpdateRepairActionButtons();
        _uninstallStatus.Text = "正在卸载…";
        try
        {
            var progress = new Progress<string>(s => MainThread.BeginInvokeOnMainThread(() => _uninstallStatus.Text = s));
            var (ok, msg) = await _host.UninstallProjectionEnvironmentAsync(progress);
            _uninstallStatus.Text = msg;
            if (!ok)
                await DisplayAlertAsync("卸载", msg, "确定");
            RefreshUninstallPanel();
            await RefreshEnvCheckAsync();
        }
        finally
        {
            _busy = false;
            UpdateRepairActionButtons();
            RefreshUninstallPanel();
        }
#else
        await DisplayAlertAsync("投影", "仅 Windows 主机支持", "确定");
#endif
    }
}
