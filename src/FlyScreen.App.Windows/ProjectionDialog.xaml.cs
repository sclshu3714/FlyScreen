using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers;
using FlyScreen.ScreenExtend;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen;

public partial class ProjectionDialog : Window
{
    private readonly AppHost _host;
    private readonly IProgress<string> _repairProgress;
    private readonly IProgress<string> _startProgress;
    private bool _busy;
    private bool _envReady;
    private int _envSeq;
    private bool _awaitingRepair;
    private int _displaySeq;
    private bool _suppressDisplayChange;
    private bool _displaySelectionReady;
    private bool _optionsExpanded;
    private DisplayInfoPayload? _peerDisplay;

    public ProjectionDialog(AppHost host)
    {
        _host = host;
        InitializeComponent();
        var icon = AppIconImage.Load();
        if (icon != null)
            Icon = icon;
        ThemeService.ApplyToChildWindow(this);
        StartBtn.IsEnabled = false;
        _repairProgress = new Progress<string>(s =>
        {
            if (EnvStatus != null)
                EnvStatus.Text = s;
            if (EnvTitleStatus != null && _host.IsProjectionEnvironmentRepairing)
                EnvTitleStatus.Text = "正在修复…";
        });
        _startProgress = new Progress<string>(s =>
        {
            if (StartProgress != null)
                StartProgress.Text = s ?? "";
        });

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OptionsHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetOptionsExpanded(!_optionsExpanded);

    private void SetOptionsExpanded(bool expanded)
    {
        _optionsExpanded = expanded;
        if (OptionsBody != null)
            OptionsBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (OptionsChevron != null)
            OptionsChevron.Text = expanded ? "▾" : "▸";
        if (OptionsSummary != null)
            OptionsSummary.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        if (!expanded)
            UpdateOptionsSummary();
    }

    private void FeatureCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateOptionsSummary();

    private void UpdateOptionsSummary()
    {
        if (OptionsSummary == null) return;
        try
        {
            var choice = ReadDisplayChoice();
            var parts = new List<string>
            {
                choice.ResolutionLabel,
                choice.ScaleLabel,
                $"{choice.Fps} 帧",
                ProjectionDisplayPresets.JpegQualityLabel(choice.JpegQuality)
            };
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

    /// <summary>标题栏只显示短态；详情留在选项内「环境」区。</summary>
    private bool _envTitleCanRepair;

    private string EnvTitleLabel(bool ready, string? hint = null)
    {
        if (_host.IsProjectionEnvironmentRepairing)
            return "正在修复…";
        var h = (hint ?? "").Trim();
        if (h.StartsWith("正在检查", StringComparison.Ordinal))
            return "正在检查…";
        return ready ? "环境正常" : "环境异常";
    }

    private void EnvTitleStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_envTitleCanRepair || _busy || _host.IsProjectionEnvironmentRepairing || _awaitingRepair) return;
        if (!_optionsExpanded)
            SetOptionsExpanded(true);
        RepairActions.Visibility = Visibility.Visible;
        _ = AwaitRepairAsync(startIfNeeded: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ApplyToChildWindow(this);
        ModeCombo.Items.Clear();
        foreach (var m in DisplayProjectionModeInfo.PickerModes)
            ModeCombo.Items.Add(new ModeItem(m));
        var prefer = DisplayProjectionModeInfo.ClampForPicker(_host.Settings.ScreenExtendProjectionMode);
        ModeCombo.SelectedIndex = prefer == DisplayProjectionMode.Clone ? 0 : 1;
        BuildScaleCombo();
        BuildRateCombos();
        AudioSyncCheck.IsChecked = _host.Settings.ScreenExtendAudioSync;
        RemoteControlCheck.IsChecked = _host.Settings.ScreenExtendRemoteControl;
        CropBlackBarsCheck.IsChecked = _host.Settings.ScreenExtendCropBlackBars;
        RefreshTargets();
        UpdateHints();
        ApplyEnvChrome();
        _ = RefreshDisplayDefaultsAsync();
        RefreshUninstallPanel();

        MttVddRepairSession.AttachProgress(_repairProgress);
        MttVddRepairSession.Changed += OnRepairSessionChanged;
        if (ReadMode() == DisplayProjectionMode.Clone)
            ApplyCloneEnvReady();
        else
            _ = BootstrapEnvAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        MttVddRepairSession.Changed -= OnRepairSessionChanged;
        MttVddRepairSession.DetachProgress(_repairProgress);
        RememberChoices();
        // 不取消修复：后台继续安装
    }

    private void OnRepairSessionChanged()
        => Dispatcher.BeginInvoke(new Action(UpdateRepairActionButtons));

    private async Task BootstrapEnvAsync()
    {
        if (_host.IsProjectionEnvironmentRepairing)
        {
            // 重开弹窗：附着正在进行的修复，按钮保持禁用
            RepairActions.Visibility = Visibility.Visible;
            EnvHint.Text = "正在自动修复虚拟显示驱动环境，请勿重复操作…";
            EnvStatus.Text = string.IsNullOrWhiteSpace(_host.ProjectionEnvironmentRepairStatus)
                ? "正在修复…"
                : _host.ProjectionEnvironmentRepairStatus;
            _envReady = false;
            UpdateRepairActionButtons();
            UpdateStartEnabled();
            await AwaitRepairAsync(startIfNeeded: false).ConfigureAwait(true);
            return;
        }

        await RefreshEnvCheckAsync().ConfigureAwait(true);
    }

    private sealed class ModeItem(DisplayProjectionMode mode)
    {
        public DisplayProjectionMode Mode { get; } = mode;
        public override string ToString() => DisplayProjectionModeInfo.Label(Mode);
    }

    private DisplayProjectionMode ReadMode()
        => ModeCombo.SelectedItem is ModeItem mi ? mi.Mode : DisplayProjectionMode.Extend;

    private ProjectionTarget? ReadTarget()
        => TargetCombo.SelectedItem as ProjectionTarget;

    private void RefreshTargets()
    {
        var prefer = (_host.Settings.ScreenExtendPeer ?? "").Trim();
        // 主窗投影：只列电脑布局局域网电脑（不含设备管理远程/中转）
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
                    && pt.Kind == ProjectionTargetKind.Peer
                    && string.Equals(pt.Id, prefer, StringComparison.OrdinalIgnoreCase))
                {
                    TargetCombo.SelectedIndex = i;
                    UpdateHints();
                    return;
                }
            }
        }

        if (TargetCombo.Items.Count > 0)
            TargetCombo.SelectedIndex = 0;
        UpdateHints();
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateHints();
        ApplyEnvChrome();
        _ = RefreshDisplayDefaultsAsync();
        if (_host.IsProjectionEnvironmentRepairing) return;
        if (ReadMode() == DisplayProjectionMode.Clone)
            ApplyCloneEnvReady();
        else
            _ = RefreshEnvCheckAsync();
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateHints();
        _ = RefreshDisplayDefaultsAsync();
        if (_host.IsProjectionEnvironmentRepairing) return;
        if (ReadMode() == DisplayProjectionMode.Clone)
            ApplyCloneEnvReady();
        else
            _ = RefreshEnvCheckAsync();
    }

    private void ApplyEnvChrome()
    {
        var showEnv = ReadMode() == DisplayProjectionMode.Extend;
        if (EnvTitleStatus != null)
            EnvTitleStatus.Visibility = showEnv ? Visibility.Visible : Visibility.Collapsed;
        if (EnvPanel != null)
            EnvPanel.Visibility = showEnv ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyCloneEnvReady()
    {
        ApplyEnvChrome();
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

    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDisplayChange) return;
        UpdateDisplayHint();
    }

    private void BuildScaleCombo()
    {
        if (ScaleCombo == null) return;
        ScaleCombo.Items.Clear();
        foreach (var s in ProjectionDisplayPresets.ScalePercents)
            ScaleCombo.Items.Add(new ScaleItem(s));
    }

    private void BuildRateCombos()
    {
        if (FpsCombo == null || QualityCombo == null) return;
        FpsCombo.Items.Clear();
        foreach (var fps in ProjectionDisplayPresets.FrameRates)
            FpsCombo.Items.Add(new NamedIntItem(fps, ProjectionDisplayPresets.FpsLabel(fps)));
        SelectNamed(FpsCombo, ProjectionDisplayPresets.RememberedFps(_host.Settings));

        QualityCombo.Items.Clear();
        foreach (var q in ProjectionDisplayPresets.JpegQualities)
            QualityCombo.Items.Add(new NamedIntItem(q, ProjectionDisplayPresets.JpegQualityLabel(q)));
        SelectNamed(QualityCombo, ProjectionDisplayPresets.RememberedJpegQuality(_host.Settings));
    }

    private static void SelectNamed(System.Windows.Controls.ComboBox combo, int value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is NamedIntItem item && item.Value == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static int ReadNamed(System.Windows.Controls.ComboBox? combo, int fallback)
        => combo?.SelectedItem is NamedIntItem item ? item.Value : fallback;

    private async Task RefreshDisplayDefaultsAsync()
    {
        if (ResolutionCombo == null || ScaleCombo == null) return;
        var seq = ++_displaySeq;
        var mode = ReadMode();
        var target = ReadTarget();
        var local = _host.GetLocalDisplayInfo();
        _peerDisplay = null;

        if (mode == DisplayProjectionMode.Extend
            && target?.Kind == ProjectionTargetKind.Peer
            && _host.Connections.GetPeerStatus(target.Id).Bidirectional)
        {
            var peerInfo = await _host.QueryPeerDisplayAsync(target.Id).ConfigureAwait(true);
            if (seq != _displaySeq) return;
            _peerDisplay = peerInfo;
        }

        if (seq != _displaySeq) return;
        ApplyDisplayComboItems(mode, local, _peerDisplay);
        UpdateDisplayHint();
    }

    private void ApplyDisplayComboItems(
        DisplayProjectionMode mode,
        DisplayInfoPayload local,
        DisplayInfoPayload? peer)
    {
        _suppressDisplayChange = true;
        try
        {
            var previous = ResolutionCombo.SelectedItem as ResItem;
            ResolutionCombo.Items.Clear();
            var hostItem = new ResItem(
                ProjectionDisplaySource.HostPrimary,
                local.Width, local.Height, local.RefreshHz,
                $"本机主屏 {local.Width}×{local.Height}");
            ResolutionCombo.Items.Add(hostItem);

            ResItem? peerItem = null;
            if (peer is { Width: > 0, Height: > 0 })
            {
                peerItem = new ResItem(
                    ProjectionDisplaySource.TargetPeer,
                    peer.Width, peer.Height, peer.RefreshHz,
                    $"目标电脑 {peer.Width}×{peer.Height}");
                ResolutionCombo.Items.Add(peerItem);
            }
            else if (mode == DisplayProjectionMode.Extend)
            {
                peerItem = new ResItem(
                    ProjectionDisplaySource.TargetPeer,
                    local.Width, local.Height, local.RefreshHz,
                    "目标电脑（连接后自动）");
                ResolutionCombo.Items.Add(peerItem);
            }

            foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
            {
                if ((w == local.Width && h == local.Height)
                    || (peer != null && w == peer.Width && h == peer.Height))
                    continue;
                ResolutionCombo.Items.Add(new ResItem(
                    ProjectionDisplaySource.Custom, w, h, 60, $"{w}×{h}"));
            }

            if (!SelectRememberedResolution(previous))
            {
                if (mode == DisplayProjectionMode.Clone)
                    ResolutionCombo.SelectedItem = hostItem;
                else if (peerItem != null)
                    ResolutionCombo.SelectedItem = peerItem;
                else
                    ResolutionCombo.SelectedIndex = 0;
            }

            var scale = _host.Settings.ScreenExtendScalePercent > 0
                ? _host.Settings.ScreenExtendScalePercent
                : mode == DisplayProjectionMode.Clone
                    ? local.ScalePercent
                    : (peer?.ScalePercent > 0 ? peer.ScalePercent : local.ScalePercent);
            if (ScaleCombo.SelectedItem is ScaleItem current && _displaySelectionReady)
                scale = current.Percent;
            scale = ProjectionDisplayPresets.ClampScale(scale);
            SelectScale(scale);
            _displaySelectionReady = true;
        }
        finally
        {
            _suppressDisplayChange = false;
        }
    }

    private bool SelectRememberedResolution(ResItem? previous)
    {
        if (_displaySelectionReady && previous != null
            && SelectResolution(previous.Source, previous.Width, previous.Height))
            return true;

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
        for (var i = 0; i < ResolutionCombo.Items.Count; i++)
        {
            if (ResolutionCombo.Items[i] is not ResItem ri) continue;
            if (ri.Width != width || ri.Height != height) continue;
            if (ri.Source == source)
            {
                ResolutionCombo.SelectedIndex = i;
                return true;
            }

            if (sizeMatch < 0)
                sizeMatch = i;
        }

        if (sizeMatch < 0) return false;
        ResolutionCombo.SelectedIndex = sizeMatch;
        return true;
    }

    private void RememberChoices()
    {
        if (ResolutionCombo == null || ScaleCombo == null || FpsCombo == null || QualityCombo == null)
            return;
        if (ResolutionCombo.Items.Count == 0 || FpsCombo.SelectedIndex < 0)
            return;
        var choice = ReadDisplayChoice();
        ProjectionDisplayPresets.WriteRemembered(
            _host.Settings, choice,
            AudioSyncCheck.IsChecked == true,
            RemoteControlCheck.IsChecked == true);
        _host.Settings.Save();
    }

    private void SelectScale(int percent)
    {
        for (var i = 0; i < ScaleCombo.Items.Count; i++)
        {
            if (ScaleCombo.Items[i] is ScaleItem si && si.Percent == percent)
            {
                ScaleCombo.SelectedIndex = i;
                return;
            }
        }

        if (ScaleCombo.Items.Count > 0)
            ScaleCombo.SelectedIndex = 0;
    }

    private ProjectionDisplayChoice ReadDisplayChoice()
    {
        var local = _host.GetLocalDisplayInfo();
        var res = ResolutionCombo.SelectedItem as ResItem;
        var scale = ScaleCombo.SelectedItem is ScaleItem si ? si.Percent : 100;
        if (res == null)
        {
            return new ProjectionDisplayChoice
            {
                Source = ProjectionDisplaySource.HostPrimary,
                Width = local.Width,
                Height = local.Height,
                ScalePercent = scale,
                RefreshHz = local.RefreshHz,
                CropBlackBars = CropBlackBarsCheck.IsChecked == true,
                Fps = ReadNamed(FpsCombo, ProjectionDisplayPresets.DefaultFps),
                JpegQuality = ReadNamed(QualityCombo, ProjectionDisplayPresets.DefaultJpegQuality)
            };
        }

        return new ProjectionDisplayChoice
        {
            Source = res.Source,
            Width = res.Width,
            Height = res.Height,
            ScalePercent = ProjectionDisplayPresets.ClampScale(scale),
            RefreshHz = res.RefreshHz > 0 ? res.RefreshHz : 60,
            CropBlackBars = CropBlackBarsCheck.IsChecked == true,
            Fps = ReadNamed(FpsCombo, ProjectionDisplayPresets.DefaultFps),
            JpegQuality = ReadNamed(QualityCombo, ProjectionDisplayPresets.DefaultJpegQuality)
        };
    }

    private void UpdateDisplayHint()
    {
        if (DisplayHint == null) return;
        var mode = ReadMode();
        var choice = ReadDisplayChoice();
        var rate = $"{choice.Fps} 帧 · {ProjectionDisplayPresets.JpegQualityLabel(choice.JpegQuality)}";
        if (mode == DisplayProjectionMode.Clone)
            DisplayHint.Text = $"复制本机主屏，不改变主屏。当前 {choice.ResolutionLabel} · {choice.ScaleLabel} · {rate}。";
        else if (choice.Source == ProjectionDisplaySource.TargetPeer)
            DisplayHint.Text = $"扩展副屏设为目标电脑 {choice.ResolutionLabel} · {rate}，主屏不变。";
        else
            DisplayHint.Text = $"扩展副屏设为 {choice.ResolutionLabel} · {choice.ScaleLabel} · {rate}，主屏不变。";
        UpdateOptionsSummary();
    }

    private void UpdateHints()
    {
        if (ModeHint == null || TargetHint == null) return;
        var mode = ReadMode();
        ModeHint.Text = DisplayProjectionModeInfo.Description(mode);

        var t = ReadTarget();
        if (t == null)
            TargetHint.Text = "暂无布局内电脑。请先把局域网电脑加入「电脑布局」（远程/中转请用设备管理投影）。";
        else if (t.Kind == ProjectionTargetKind.Peer
                 && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
            TargetHint.Text = "该电脑在布局中但尚未连通，请等待连接后再投影。";
        else if (mode == DisplayProjectionMode.Extend)
            TargetHint.Text = "扩展到该电脑：需本机真副屏或虚拟显示驱动。";
        else
            TargetHint.Text = "复制：将本机主屏镜像到该电脑（无需虚拟驱动）。";
        UpdateDisplayHint();
    }

    private sealed class ResItem(
        ProjectionDisplaySource source,
        int width,
        int height,
        int refreshHz,
        string label)
    {
        public ProjectionDisplaySource Source { get; } = source;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int RefreshHz { get; } = refreshHz;
        public override string ToString() => label;
    }

    private sealed class ScaleItem(int percent)
    {
        public int Percent { get; } = percent;
        public override string ToString() => $"{percent}%";
    }

    private sealed class NamedIntItem(int value, string label)
    {
        public int Value { get; } = value;
        public override string ToString() => label;
    }

    private async Task RefreshUninstallPanelAsync()
    {
        if (UninstallBtn == null) return;
        var show = false;
        if (!_host.IsProjectionEnvironmentRepairing)
        {
            // 环境检查通过直接显示；不通过时若已装 VDD/控制台也显示
            var installed = await Task.Run(() =>
                MttVddEnvironment.IsProductInstalled()
                || MttVddInstaller.HasAnyControlExecutable()
                || MttVddInstaller.HasInstalledControlExecutable()).ConfigureAwait(true);
            show = _envReady || installed;
        }

        UninstallBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (UninstallStatus != null && !show)
            UninstallStatus.Text = "";
    }

    private void RefreshUninstallPanel()
        => _ = RefreshUninstallPanelAsync();

    private void UpdateStartEnabled()
    {
        if (StartBtn == null) return;
        var t = ReadTarget();
        var peerReady = t is not { Kind: ProjectionTargetKind.Peer }
                        || _host.Connections.GetPeerStatus(t.Id).Bidirectional;
        StartBtn.IsEnabled = !_busy && _envReady && peerReady && !_host.IsProjectionEnvironmentRepairing;
    }

    private void UpdateRepairActionButtons()
    {
        var repairing = _host.IsProjectionEnvironmentRepairing || _awaitingRepair;
        if (RepairBtn != null)
            RepairBtn.IsEnabled = !repairing && !_busy;
        if (RecheckBtn != null)
            RecheckBtn.IsEnabled = !repairing && !_busy;
        if (UninstallBtn != null)
            UninstallBtn.IsEnabled = !repairing && !_busy;
        UpdateStartEnabled();
    }

    private void SetEnvUi(string hint, bool ready, bool showRepair, string? status = null)
    {
        _envReady = ready;
        EnvHint.Text = hint;
        if (status != null || !_host.IsProjectionEnvironmentRepairing)
            EnvStatus.Text = status ?? "";
        var show = showRepair || _host.IsProjectionEnvironmentRepairing;
        RepairActions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (EnvTitleStatus != null)
        {
            var label = EnvTitleLabel(ready, hint);
            EnvTitleStatus.Text = label;
            _envTitleCanRepair = show && !_host.IsProjectionEnvironmentRepairing
                                 && label == "环境异常";
            EnvTitleStatus.Cursor = _envTitleCanRepair
                ? System.Windows.Input.Cursors.Hand
                : System.Windows.Input.Cursors.Arrow;
            EnvTitleStatus.ToolTip = _envTitleCanRepair ? "点击自动修复环境" : null;
            // 正常绿 / 异常红
            var hex = ready && !_host.IsProjectionEnvironmentRepairing
                ? "#059669"
                : label is "正在检查…" or "正在修复…"
                    ? "#6B7280"
                    : "#B91C1C";
            EnvTitleStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        }

        if (show && !_optionsExpanded)
            SetOptionsExpanded(true);
        UpdateRepairActionButtons();
        RefreshUninstallPanel();
    }

    private async Task RefreshEnvCheckAsync()
    {
        if (EnvPanel == null) return;
        if (_host.IsProjectionEnvironmentRepairing)
        {
            RepairActions.Visibility = Visibility.Visible;
            EnvStatus.Text = string.IsNullOrWhiteSpace(_host.ProjectionEnvironmentRepairStatus)
                ? "正在修复…"
                : _host.ProjectionEnvironmentRepairStatus;
            if (!_optionsExpanded)
                SetOptionsExpanded(true);
            UpdateRepairActionButtons();
            return;
        }

        var seq = ++_envSeq;
        var mode = ReadMode();
        var t = ReadTarget();

        if (mode == DisplayProjectionMode.Clone)
        {
            if (seq != _envSeq) return;
            ApplyCloneEnvReady();
            return;
        }

        SetEnvUi("正在检查环境…", ready: false, showRepair: false);

        if (t == null)
        {
            if (seq != _envSeq) return;
            SetEnvUi("请选择电脑布局中的目标电脑。", false, false);
            return;
        }

        if (t.Kind == ProjectionTargetKind.Peer
            && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
        {
            if (seq != _envSeq) return;
            SetEnvUi($"「{t.DisplayName}」尚未连通，无法投影。", false, false);
            return;
        }

        var (ready, detail) = await MttVddEnvironment.CheckExtendReadyAsync().ConfigureAwait(true);
        if (seq != _envSeq) return;
        if (ready)
        {
            SetEnvUi(detail, true, false);
            return;
        }

        SetEnvUi(detail, false, showRepair: true);
    }

    private async Task AwaitRepairAsync(bool startIfNeeded)
    {
        if (_awaitingRepair) return;
        _awaitingRepair = true;
        UpdateRepairActionButtons();
        try
        {
            if (startIfNeeded)
                EnvStatus.Text = "正在修复…";
            var (_, msg) = await _host.RepairProjectionEnvironmentAsync(_repairProgress).ConfigureAwait(true);
            EnvStatus.Text = msg;
            RefreshUninstallPanel();
            await RefreshEnvCheckAsync().ConfigureAwait(true);
        }
        finally
        {
            _awaitingRepair = false;
            UpdateRepairActionButtons();
            RefreshUninstallPanel();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_envReady || _host.IsProjectionEnvironmentRepairing) return;
        _busy = true;
        UpdateRepairActionButtons();
        if (StartProgress != null)
            StartProgress.Text = "准备开始…";
        try
        {
            var result = await _host.StartProjectionAsync(
                ReadTarget(), ReadMode(), ReadDisplayChoice(),
                AudioSyncCheck.IsChecked == true,
                RemoteControlCheck.IsChecked == true,
                _startProgress)
                .ConfigureAwait(true);
            if (result.NeedEnvironmentRepair)
            {
                if (StartProgress != null)
                    StartProgress.Text = "失败：需要修复环境";
                SetEnvUi(result.Message ?? "需要安装虚拟显示驱动", false, true, "请点击「自动修复环境」");
                return;
            }

            if (!result.Ok)
            {
                if (StartProgress != null)
                    StartProgress.Text = "失败：" + (result.Message ?? "投影失败");
                MessageBox.Show(this, result.Message ?? "投影失败", "投影",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (StartProgress != null)
                StartProgress.Text = "成功";
            DialogResult = true;
            Close();
        }
        finally
        {
            _busy = false;
            UpdateRepairActionButtons();
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _host.IsProjectionEnvironmentRepairing || _awaitingRepair) return;
        RepairActions.Visibility = Visibility.Visible;
        await AwaitRepairAsync(startIfNeeded: true).ConfigureAwait(true);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _host.IsProjectionEnvironmentRepairing) return;
        var confirm = MessageBox.Show(this,
            "将卸载本程序安装的 Virtual Display Driver，并尽量清理相关程序与数据。系统原先已有的驱动不会被本标记覆盖卸载。是否继续？",
            "卸载虚拟显示驱动",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        UpdateRepairActionButtons();
        UninstallStatus.Text = "正在卸载…";
        try
        {
            var progress = new Progress<string>(s => UninstallStatus.Text = s);
            var (ok, msg) = await _host.UninstallProjectionEnvironmentAsync(progress).ConfigureAwait(true);
            UninstallStatus.Text = msg;
            RefreshUninstallPanel();
            if (!ok)
            {
                MessageBox.Show(this, msg, "卸载", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _busy = false;
            UpdateRepairActionButtons();
            await RefreshEnvCheckAsync().ConfigureAwait(true);
            RefreshUninstallPanel();
        }
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (_host.IsProjectionEnvironmentRepairing || _awaitingRepair) return;
        await RefreshEnvCheckAsync().ConfigureAwait(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
