using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.DesktopUi;

public partial class ProjectionDialog : Window
{
    private readonly IDesktopShellHost _host;
    private bool _busy;
    private bool _envReady;
    private int _envSeq;
    private bool _displaySelectionReady;

    public ProjectionDialog() : this(null!) { }

    public ProjectionDialog(IDesktopShellHost host)
    {
        _host = host;
        InitializeComponent();
        TryApplyWindowIcon();
        LayoutUpdated += (_, _) => SyncBodyWidth();
        DesktopThemeService.Apply(DesktopThemeService.Current, this);
        StartBtn.IsEnabled = false;
        Opened += (_, _) =>
        {
            if (_host == null) return;
            SetOptionsExpanded(false);
            DesktopThemeService.Apply(DesktopThemeService.Current, this);
            ModeCombo.Items.Clear();
            foreach (var m in DisplayProjectionModeInfo.PickerModes)
                ModeCombo.Items.Add(DisplayProjectionModeInfo.Label(m));
            var prefer = DisplayProjectionModeInfo.ClampForPicker(_host.Settings.ScreenExtendProjectionMode);
            ModeCombo.SelectedIndex = prefer == DisplayProjectionMode.Clone ? 0 : 1;
            ScaleCombo.Items.Clear();
            foreach (var s in ProjectionDisplayPresets.ScalePercents)
                ScaleCombo.Items.Add($"{s}%");
            var savedScale = _host.Settings.ScreenExtendScalePercent > 0
                ? ProjectionDisplayPresets.ClampScale(_host.Settings.ScreenExtendScalePercent)
                : ProjectionDisplayPresets.ScalePercents[0];
            ScaleCombo.SelectedIndex = IndexOf(ProjectionDisplayPresets.ScalePercents, savedScale);
            FpsCombo.Items.Clear();
            foreach (var fps in ProjectionDisplayPresets.FrameRates)
                FpsCombo.Items.Add(ProjectionDisplayPresets.FpsLabel(fps));
            FpsCombo.SelectedIndex = IndexOf(
                ProjectionDisplayPresets.FrameRates, ProjectionDisplayPresets.RememberedFps(_host.Settings));
            QualityCombo.Items.Clear();
            foreach (var q in ProjectionDisplayPresets.JpegQualities)
                QualityCombo.Items.Add(ProjectionDisplayPresets.JpegQualityLabel(q));
            QualityCombo.SelectedIndex = IndexOf(
                ProjectionDisplayPresets.JpegQualities, ProjectionDisplayPresets.RememberedJpegQuality(_host.Settings));
            AudioSyncCheck.IsChecked = _host.Settings.ScreenExtendAudioSync;
            RemoteControlCheck.IsChecked = _host.Settings.ScreenExtendRemoteControl;
            CropBlackBarsCheck.IsChecked = _host.Settings.ScreenExtendCropBlackBars;
            AudioSyncCheck.IsCheckedChanged += (_, _) => UpdateOptionsSummary();
            RemoteControlCheck.IsCheckedChanged += (_, _) => UpdateOptionsSummary();
            CropBlackBarsCheck.IsCheckedChanged += (_, _) => UpdateOptionsSummary();
            ResolutionCombo.SelectionChanged += (_, _) => UpdateOptionsSummary();
            ScaleCombo.SelectionChanged += (_, _) => UpdateOptionsSummary();
            FpsCombo.SelectionChanged += (_, _) => UpdateOptionsSummary();
            QualityCombo.SelectionChanged += (_, _) => UpdateOptionsSummary();
            Closed += (_, _) => RememberChoices();
            RefreshTargets();
            RefreshDisplayPresets();
            UpdateHints();
            ApplyEnvChrome();
            if (ReadMode() == DisplayProjectionMode.Clone)
                ApplyCloneEnvReady();
            else
                RefreshEnvCheck();
            SetOptionsExpanded(false);
        };
    }

    private void RefreshDisplayPresets()
    {
        var previous = ResolutionCombo.SelectedIndex;
        ResolutionCombo.Items.Clear();
        ResolutionCombo.Items.Add("本机主屏（自动）");
        ResolutionCombo.Items.Add("目标电脑（自动）");
        foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
            ResolutionCombo.Items.Add($"{w}×{h}");
        if (_displaySelectionReady && previous >= 0 && previous < ResolutionCombo.ItemCount)
            ResolutionCombo.SelectedIndex = previous;
        else if (!SelectRememberedResolution())
            ResolutionCombo.SelectedIndex = ReadMode() == DisplayProjectionMode.Clone ? 0 : 1;
        _displaySelectionReady = true;
        DisplayHint.Text = _host.CanStartProjection
            ? "分辨率、帧率、画质会记住上次的选择。"
            : "分辨率、帧率、画质会记住上次的选择。本端不发起投影，可接收来自其他电脑的画面。";
    }

    private bool SelectRememberedResolution()
    {
        var settings = _host.Settings;
        if (settings.ScreenExtendFps <= 0 && settings.ScreenExtendWidth <= 0)
            return false;

        if (settings.ScreenExtendWidth > 0 && settings.ScreenExtendHeight > 0)
        {
            var label = $"{settings.ScreenExtendWidth}×{settings.ScreenExtendHeight}";
            for (var i = 0; i < ResolutionCombo.ItemCount; i++)
            {
                if (ResolutionCombo.Items[i]?.ToString() == label)
                {
                    ResolutionCombo.SelectedIndex = i;
                    return true;
                }
            }
        }

        ResolutionCombo.SelectedIndex = settings.ScreenExtendDisplaySource == (int)ProjectionDisplaySource.TargetPeer
            ? 1
            : 0;
        return true;
    }

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] == value) return i;
        }

        return 0;
    }

    private void RememberChoices()
    {
        if (_host == null || ResolutionCombo == null || FpsCombo == null || QualityCombo == null || ScaleCombo == null)
            return;
        if (ResolutionCombo.SelectedIndex < 0 || FpsCombo.SelectedIndex < 0)
            return;

        var choice = new ProjectionDisplayChoice
        {
            ScalePercent = ValueAt(ProjectionDisplayPresets.ScalePercents, ScaleCombo.SelectedIndex, 100),
            Fps = ValueAt(ProjectionDisplayPresets.FrameRates, FpsCombo.SelectedIndex, ProjectionDisplayPresets.DefaultFps),
            JpegQuality = ValueAt(
                ProjectionDisplayPresets.JpegQualities,
                QualityCombo.SelectedIndex,
                ProjectionDisplayPresets.DefaultJpegQuality),
            CropBlackBars = CropBlackBarsCheck.IsChecked == true
        };
        var index = ResolutionCombo.SelectedIndex;
        if (index <= 0)
            choice.Source = ProjectionDisplaySource.HostPrimary;
        else if (index == 1)
            choice.Source = ProjectionDisplaySource.TargetPeer;
        else
        {
            var presetIndex = index - 2;
            if (presetIndex >= 0 && presetIndex < ProjectionDisplayPresets.Resolutions.Count)
            {
                var preset = ProjectionDisplayPresets.Resolutions[presetIndex];
                choice.Source = ProjectionDisplaySource.Custom;
                choice.Width = preset.W;
                choice.Height = preset.H;
            }
        }

        ProjectionDisplayPresets.WriteRemembered(
            _host.Settings, choice,
            AudioSyncCheck.IsChecked == true,
            RemoteControlCheck.IsChecked == true);
        _host.Settings.Save();
    }

    private static int ValueAt(IReadOnlyList<int> values, int index, int fallback)
        => index >= 0 && index < values.Count ? values[index] : fallback;

    private void TryApplyWindowIcon()
    {
        try
        {
            var uri = new Uri("avares://FlyScreen.App.Desktop/Assets/app_icon.png");
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));
        }
        catch
        {
            try
            {
                var uri = new Uri("avares://FlyScreen.App.Desktop/Assets/wlogo_tu.ico");
                Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));
            }
            catch
            {
                // 保持 XAML Icon
            }
        }
    }

    private void RefreshTargets()
    {
        var prefer = (_host.Settings.ScreenExtendPeer ?? "").Trim();
        var items = ProjectionTargets.ListForPicker(_host.Connections, null, null);
        TargetCombo.Items.Clear();
        foreach (var t in items)
            TargetCombo.Items.Add(t);

        // 仅当上次选择仍在本次列表中时才预选（列表本身已排除远程/中转）
        if (!string.IsNullOrWhiteSpace(prefer))
        {
            for (var i = 0; i < TargetCombo.Items.Count; i++)
            {
                if (TargetCombo.Items[i] is ProjectionTarget pt
                    && string.Equals(pt.Id, prefer, StringComparison.OrdinalIgnoreCase))
                {
                    TargetCombo.SelectedIndex = i;
                    return;
                }
            }
        }

        if (TargetCombo.Items.Count > 0)
            TargetCombo.SelectedIndex = 0;
    }

    private DisplayProjectionMode ReadMode()
        => ModeCombo.SelectedIndex == 0 ? DisplayProjectionMode.Clone : DisplayProjectionMode.Extend;

    private ProjectionTarget? ReadTarget()
        => TargetCombo.SelectedItem as ProjectionTarget;

    private void Mode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        UpdateHints();
        ApplyEnvChrome();
        RefreshDisplayPresets();
        if (ReadMode() == DisplayProjectionMode.Clone)
            ApplyCloneEnvReady();
        else
            RefreshEnvCheck();
    }

    private void Target_Changed(object? sender, SelectionChangedEventArgs e)
    {
        UpdateHints();
        if (ReadMode() == DisplayProjectionMode.Clone)
            ApplyCloneEnvReady();
        else
            RefreshEnvCheck();
    }

    private void ApplyEnvChrome()
    {
        var showEnv = ReadMode() == DisplayProjectionMode.Extend;
        if (EnvTitleStatus != null)
            EnvTitleStatus.IsVisible = showEnv;
        if (EnvPanel != null)
            EnvPanel.IsVisible = showEnv;
    }

    private void ApplyCloneEnvReady()
    {
        ApplyEnvChrome();
        if (_host == null) return;
        var t = ReadTarget();
        if (t == null)
            _envReady = false;
        else if (t.Kind == ProjectionTargetKind.Peer
                 && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
            _envReady = false;
        else
            _envReady = true;
        UpdateStartEnabled();
    }

    private void UpdateHints()
    {
        if (ModeHint == null || TargetHint == null || _host == null) return;
        var mode = ReadMode();
        ModeHint.Text = DisplayProjectionModeInfo.Description(mode);

        var t = ReadTarget();
        if (t == null)
            TargetHint.Text = "暂无布局内电脑。请先把局域网电脑加入「电脑布局」（远程/中转请用设备管理投影）。";
        else if (t.Kind == ProjectionTargetKind.Peer
                 && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
            TargetHint.Text = "该电脑在布局中但尚未连通，请等待连接后再投影。";
        else if (_host.CanStartProjection && mode == DisplayProjectionMode.Extend)
            TargetHint.Text = "扩展：采集虚拟副屏或已连接的第二块屏幕，推到所选电脑。";
        else if (_host.CanStartProjection)
            TargetHint.Text = "复制：采集本机主屏，推到所选电脑。";
        else if (mode == DisplayProjectionMode.Extend)
            TargetHint.Text = "本端不发起扩展投影；请在 Windows 或 Mac 主机操作。";
        else
            TargetHint.Text = "本端不发起复制投影；可接收来自 Windows 或 Mac 的投影。";
    }

    private void UpdateStartEnabled()
    {
        if (StartBtn == null || _host == null) return;
        var t = ReadTarget();
        var peerReady = t is not { Kind: ProjectionTargetKind.Peer }
                        || _host.Connections.GetPeerStatus(t.Id).Bidirectional;
        StartBtn.IsEnabled = !_busy && _envReady && peerReady;
        StartBtn.Opacity = StartBtn.IsEnabled ? 1 : 0.5;
    }

    private bool _optionsExpanded;

    /// <summary>
    /// 选项展开后，内部提示文字的期望宽度会大于滚动区。把内容锁在视口宽度内，卡片才不会比上面两张更宽。
    /// </summary>
    private void SyncBodyWidth()
    {
        if (BodyScroll == null || BodyGrid == null) return;
        var width = BodyScroll.Viewport.Width;
        if (width < 1)
            width = BodyScroll.Bounds.Width - BodyScroll.Padding.Left - BodyScroll.Padding.Right;
        if (width < 1 || (!double.IsNaN(BodyGrid.MaxWidth) && Math.Abs(BodyGrid.MaxWidth - width) < 0.5))
            return;
        BodyGrid.MaxWidth = width;
        // 星号列会按无限宽测量，不换行的期望宽度会把卡片撑出滚动区。锁到卡片内宽，长说明才会换行。
        var textWidth = width - 58;
        if (textWidth < 1) return;
        if (DisplayHint != null) DisplayHint.MaxWidth = textWidth;
        if (FeatureHint != null) FeatureHint.MaxWidth = textWidth;
        if (EnvHint != null) EnvHint.MaxWidth = textWidth;
        if (EnvStatus != null) EnvStatus.MaxWidth = textWidth;
    }

    private void OptionsToggle_Click(object? sender, RoutedEventArgs e)
        => SetOptionsExpanded(!_optionsExpanded);

    private void SetOptionsExpanded(bool expanded)
    {
        _optionsExpanded = expanded;
        if (OptionsBody != null)
            OptionsBody.IsVisible = expanded;
        if (OptionsToggleBtn != null)
            OptionsToggleBtn.Content = expanded ? "▾" : "▸";
        if (OptionsSummary != null)
            OptionsSummary.IsVisible = !expanded;
        if (!expanded)
            UpdateOptionsSummary();
    }

    private void UpdateOptionsSummary()
    {
        if (OptionsSummary == null || ResolutionCombo == null) return;
        try
        {
            var res = ResolutionCombo.SelectedItem?.ToString() ?? "";
            var scale = ScaleCombo.SelectedItem?.ToString() ?? "";
            var fps = FpsCombo.SelectedItem?.ToString() ?? "";
            var quality = QualityCombo.SelectedItem?.ToString() ?? "";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(res)) parts.Add(res);
            if (!string.IsNullOrWhiteSpace(scale)) parts.Add(scale);
            if (!string.IsNullOrWhiteSpace(fps)) parts.Add(fps);
            if (!string.IsNullOrWhiteSpace(quality)) parts.Add(quality);
            if (AudioSyncCheck?.IsChecked == true) parts.Add("声音同步");
            if (RemoteControlCheck?.IsChecked == true) parts.Add("远程控制");
            if (CropBlackBarsCheck?.IsChecked == true) parts.Add("去黑边");
            OptionsSummary.Text = string.Join(" · ", parts);
        }
        catch
        {
            OptionsSummary.Text = "";
        }
    }

    private void SetEnvUi(string hint, bool ready, string? status = null)
    {
        _envReady = ready;
        EnvHint.Text = hint;
        EnvStatus.Text = status ?? "";
        if (EnvTitleStatus != null)
        {
            var h = (hint ?? "").Trim();
            var text = h.StartsWith("正在检查", StringComparison.Ordinal)
                ? "正在检查…"
                : ready ? "环境正常" : "环境异常";
            EnvTitleStatus.Text = text;
            EnvTitleStatus.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(ready ? "#059669"
                    : text is "正在检查…" ? "#6B7280" : "#B91C1C"));
        }

        UpdateStartEnabled();
    }

    private void RefreshEnvCheck()
    {
        if (EnvPanel == null || _host == null) return;
        var seq = ++_envSeq;
        var mode = ReadMode();
        var t = ReadTarget();

        if (mode == DisplayProjectionMode.Clone)
        {
            if (seq != _envSeq) return;
            ApplyCloneEnvReady();
            return;
        }

        SetEnvUi("正在检查环境…", ready: false);

        if (t == null)
        {
            if (seq != _envSeq) return;
            SetEnvUi("请选择电脑布局中的目标电脑。", false);
            return;
        }

        if (t.Kind == ProjectionTargetKind.Peer
            && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
        {
            if (seq != _envSeq) return;
            SetEnvUi($"「{t.DisplayName}」尚未连通，无法投影。", false);
            return;
        }

        if (seq != _envSeq) return;
        if (!_host.CanStartProjection)
        {
            SetEnvUi("本端不发起扩展投影；请在 Windows 或 Mac 主机操作。", false);
            return;
        }

        var (ready, hint) = _host.DescribeProjectionEnvironment(mode);
        if (seq != _envSeq) return;
        SetEnvUi(hint, ready);
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || !_envReady) return;
        _busy = true;
        UpdateStartEnabled();
        try
        {
            RememberChoices();
            var (ok, msg) = await _host.StartProjectionAsync(
                ReadTarget(), ReadMode(), AudioSyncCheck.IsChecked == true,
                CropBlackBarsCheck.IsChecked == true,
                RemoteControlCheck.IsChecked == true);
            if (!ok)
            {
                await ConfirmDialog.ShowAsync(this, "投影", msg ?? "投影失败", primaryText: "确定", cancelText: null);
                return;
            }

            Close(true);
        }
        finally
        {
            _busy = false;
            UpdateStartEnabled();
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
