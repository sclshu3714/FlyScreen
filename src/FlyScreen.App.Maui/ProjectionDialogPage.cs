using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;
#if WINDOWS
using FlyScreen.ScreenExtend;
#endif

namespace FlyScreen.AppMaui;

/// <summary>投影弹窗：环境检查常显；未通过前禁用开始；主题与主界面一致。手机仅复制。</summary>
public sealed class ProjectionDialogPage : ContentPage
{
    private readonly MauiAppHost _host;
    private readonly bool _phoneOnlyClone;
    private readonly Picker _modePicker = new();
    private readonly Picker _targetPicker = new();
    private readonly Picker _resolutionPicker = new();
    private readonly Picker _scalePicker = new();
    private readonly Label _modeHint = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _targetHint = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _displayHint = new()
    {
        FontSize = 12,
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "复制默认本机主屏；扩展默认目标电脑。"
    };
    private readonly List<(ProjectionDisplaySource Source, int W, int H, int Hz)> _resOptions = [];
    private int _displaySeq;
    private bool _suppressDisplayChange;
    private DisplayInfoPayload? _peerDisplay;
    private readonly VerticalStackLayout _envPanel = new() { Spacing = 8 };
    private readonly VerticalStackLayout _uninstallPanel = new() { Spacing = 8, IsVisible = false };
    private readonly Label _envHint = new()
    {
        FontSize = 12,
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "正在检查环境…"
    };
    private readonly Label _envStatus = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _uninstallStatus = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _uninstallBody = new()
    {
        Text = "检测到本程序曾安装 MTT Virtual Display Driver。可卸载并清理相关程序与数据。",
        FontSize = 12,
        LineBreakMode = LineBreakMode.WordWrap
    };
    private readonly HorizontalStackLayout _repairActions = new() { Spacing = 8, IsVisible = false };
    private Border _dialogShell = null!;
    private Border _titleBar = null!;
    private Border _modeCard = null!;
    private Border _targetCard = null!;
    private Border _displayCard = null!;
    private Border _audioCard = null!;
    private CheckBox _audioSyncCheck = null!;
    private Label _audioTitle = null!;
    private Label _audioHint = null!;
    private Border _envCard = null!;
    private Border _uninstallCard = null!;
    private Border _footerBar = null!;
    private Label _titleLabel = null!;
    private Button _closeBtn = null!;
    private Button _startBtn = null!;
    private Button _repairBtn = null!;
    private Button _recheckBtn = null!;
    private Button _uninstallBtn = null!;
    private Button _cancelBtn = null!;
    private readonly List<ProjectionTarget> _targets = [];
    private readonly IProgress<string> _repairProgress;
    private bool _busy;
    private bool _envReady;
    private int _envSeq;
    private bool _awaitingRepair;

    public ProjectionDialogPage(MauiAppHost host)
    {
        _host = host;
#if ANDROID
        _phoneOnlyClone = true;
#else
        _phoneOnlyClone = false;
#endif
        Title = "投影";
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromArgb("#80000000");
        _repairProgress = new Progress<string>(s =>
            MainThread.BeginInvokeOnMainThread(() => _envStatus.Text = s));

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

        _envPanel.Children.Add(new Label { Text = "环境检查", FontAttributes = FontAttributes.Bold });
        _envPanel.Children.Add(_envHint);
        _envPanel.Children.Add(_repairActions);
        _envPanel.Children.Add(_envStatus);

        _uninstallBtn = new Button
        {
            Text = "卸载并清理",
            BackgroundColor = Colors.Transparent,
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.Start
        };
        _uninstallBtn.Clicked += async (_, _) => await OnUninstallAsync();
        _uninstallPanel.Children.Add(new Label { Text = "卸载虚拟显示驱动", FontAttributes = FontAttributes.Bold });
        _uninstallPanel.Children.Add(_uninstallBody);
        _uninstallPanel.Children.Add(_uninstallBtn);
        _uninstallPanel.Children.Add(_uninstallStatus);

        _cancelBtn = new Button
        {
            Text = "取消",
            BackgroundColor = Colors.Transparent,
            HeightRequest = 36,
            MinimumWidthRequest = 88
        };
        _cancelBtn.Clicked += async (_, _) => await CloseAsync();

        _titleLabel = new Label
        {
            Text = "投影",
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.StartAndExpand
        };
        _closeBtn = new Button
        {
            Text = "✕",
            FontSize = 14,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 40,
            HeightRequest = 36,
            Padding = 0,
            HorizontalOptions = LayoutOptions.End
        };
        _closeBtn.Clicked += async (_, _) => await CloseAsync();

        _titleBar = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(16, 10, 8, 10),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
                Children =
                {
                    _titleLabel,
                    _closeBtn
                }
            }
        };
        Grid.SetColumn(_titleLabel, 0);
        Grid.SetColumn(_closeBtn, 1);

        _modeCard = MakeCard("投影方式", _modePicker, _modeHint);
        _targetCard = MakeCard("目标", _targetPicker, _targetHint);
        _displayCard = new Border
        {
            StrokeThickness = 1,
            Padding = new Thickness(14, 12),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label { Text = "分辨率与缩放", FontAttributes = FontAttributes.Bold },
                    _resolutionPicker,
                    _scalePicker,
                    _displayHint
                }
            }
        };
        _audioSyncCheck = new CheckBox { IsChecked = false };
        _audioTitle = new Label
        {
            Text = "声音同步",
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        };
        _audioHint = new Label
        {
            Text = "默认关闭。开启后本机不再播放声音，声音改由目标电脑播放。",
            FontSize = 12
        };
        _audioCard = new Border
        {
            StrokeThickness = 1,
            Padding = new Thickness(14, 12),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            _audioSyncCheck,
                            _audioTitle
                        }
                    },
                    _audioHint
                }
            }
        };
        _envCard = WrapCard(_envPanel);
        _uninstallCard = WrapCard(_uninstallPanel);
        _uninstallCard.IsVisible = false;
        if (_phoneOnlyClone)
        {
            _envCard.IsVisible = false;
            _uninstallCard.IsVisible = false;
        }

        _footerBar = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(16, 12),
            Content = new HorizontalStackLayout
            {
                Spacing = 10,
                HorizontalOptions = LayoutOptions.End,
                Children = { _cancelBtn, _startBtn }
            }
        };

        var bodyStack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 8, 16, 8),
            Spacing = 12,
            Children = { _modeCard, _targetCard, _displayCard, _audioCard }
        };
        if (!_phoneOnlyClone)
        {
            bodyStack.Children.Add(_envCard);
            bodyStack.Children.Add(_uninstallCard);
        }

        var body = new ScrollView
        {
            VerticalOptions = LayoutOptions.Fill,
            Content = bodyStack
        };

        _dialogShell = new Border
        {
            StrokeThickness = 1,
            MaximumWidthRequest = 480,
            MinimumWidthRequest = 320,
            MaximumHeightRequest = 640,
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
        Grid.SetRow(_titleBar, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(_footerBar, 2);

        Content = new Grid
        {
            Padding = new Thickness(24),
            Children = { _dialogShell }
        };

        ApplyTheme();
        Appearing += OnAppearing;
        Disappearing += OnDisappearing;
    }

    private void OnAppearing(object? sender, EventArgs e)
    {
        ApplyTheme();
        RefreshTargets();
        UpdateHints();
        _ = RefreshDisplayDefaultsAsync();
        if (_phoneOnlyClone)
        {
            _envReady = true;
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
        _modeHint.TextColor = muted;
        _targetHint.TextColor = muted;
        _displayHint.TextColor = muted;
        _envHint.TextColor = muted;
        _uninstallBody.TextColor = muted;
        _envStatus.TextColor = text;
        _uninstallStatus.TextColor = text;
        _titleLabel.TextColor = text;
        _closeBtn.TextColor = muted;

        _dialogShell.BackgroundColor = winBg;
        _dialogShell.Stroke = line;
        _titleBar.BackgroundColor = winBg;
        _footerBar.BackgroundColor = winBg;

        PaintCard(_modeCard, card, line, text);
        PaintCard(_targetCard, card, line, text);
        PaintCard(_displayCard, card, line, text);
        PaintCard(_audioCard, card, line, text);
        _audioHint.TextColor = muted;
        _audioTitle.TextColor = text;
        PaintCard(_envCard, subtle, line, text);
        PaintCard(_uninstallCard, subtle, line, text);
        PaintPanelLabels(_envPanel, text);
        PaintPanelLabels(_uninstallPanel, text);

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

    private void RefreshUninstallPanel()
        => _ = RefreshUninstallPanelAsync();

    private async Task RefreshUninstallPanelAsync()
    {
#if WINDOWS
        // 仅本程序安装且驱动管道已就绪时显示卸载
        var show = false;
        if (_host.IsMttInstalledByApp && !_host.IsProjectionEnvironmentRepairing)
            show = await MttVddEnvironment.IsDriverReadyAsync();
        _uninstallPanel.IsVisible = show;
        _uninstallCard.IsVisible = show;
        if (!show)
            _uninstallStatus.Text = "";
#else
        _uninstallPanel.IsVisible = false;
        _uninstallCard.IsVisible = false;
        await Task.CompletedTask;
#endif
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

            _resolutionPicker.SelectedIndex = mode == DisplayProjectionMode.Clone ? 0
                : (_resOptions.Count > 1 ? 1 : 0);

            var scale = mode == DisplayProjectionMode.Clone
                ? local.ScalePercent
                : (peer?.ScalePercent > 0 ? peer.ScalePercent : local.ScalePercent);
            scale = ProjectionDisplayPresets.ClampScale(scale);
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
                RefreshHz = local?.RefreshHz ?? 60
            };
        }

        var opt = _resOptions[i];
        return new ProjectionDisplayChoice
        {
            Source = opt.Source,
            Width = opt.W,
            Height = opt.H,
            ScalePercent = scale,
            RefreshHz = opt.Hz > 0 ? opt.Hz : 60
        };
    }

    private void UpdateDisplayHint()
    {
        var mode = ReadMode();
        var c = ReadDisplayChoice();
        if (mode == DisplayProjectionMode.Clone)
            _displayHint.Text = $"复制：当前 {c.ResolutionLabel} · {c.ScaleLabel}（不改主屏）。";
        else
            _displayHint.Text = $"扩展：副屏 {c.ResolutionLabel} · {c.ScaleLabel}，主屏不变。";
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
        _repairActions.IsVisible = showRepair || _host.IsProjectionEnvironmentRepairing;
        UpdateRepairActionButtons();
    }

    private async Task RefreshEnvCheckAsync()
    {
        if (_host.IsProjectionEnvironmentRepairing)
        {
            _repairActions.IsVisible = true;
            _envStatus.Text = string.IsNullOrWhiteSpace(_host.ProjectionEnvironmentRepairStatus)
                ? "正在修复…"
                : _host.ProjectionEnvironmentRepairStatus;
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
        UpdateRepairActionButtons();
        try
        {
            var result = await _host.StartProjectionAsync(
                ReadTarget(), ReadMode(), ReadDisplayChoice(), _audioSyncCheck.IsChecked == true);
            if (result.NeedEnvironmentRepair)
            {
                SetEnvUi(result.Message ?? "需要安装虚拟显示驱动", false, true, "请点击「自动修复环境」");
                return;
            }

            if (!result.Ok)
            {
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
