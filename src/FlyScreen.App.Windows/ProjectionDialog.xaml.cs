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
    private bool _busy;
    private bool _envReady;
    private int _envSeq;
    private bool _awaitingRepair;
    private int _displaySeq;
    private bool _suppressDisplayChange;
    private DisplayInfoPayload? _peerDisplay;

    public ProjectionDialog(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        StartBtn.IsEnabled = false;
        _repairProgress = new Progress<string>(s =>
        {
            if (EnvStatus != null)
                EnvStatus.Text = s;
        });

        Loaded += OnLoaded;
        Closed += OnClosed;
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
        RefreshTargets();
        _ = RefreshDisplayDefaultsAsync();
        RefreshUninstallPanel();

        MttVddRepairSession.AttachProgress(_repairProgress);
        MttVddRepairSession.Changed += OnRepairSessionChanged;
        _ = BootstrapEnvAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        MttVddRepairSession.Changed -= OnRepairSessionChanged;
        MttVddRepairSession.DetachProgress(_repairProgress);
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
        var prefer = _host.Settings.ScreenExtendPeer;
        var locals = MttVddEnvironment.ListLocalSecondaryLabels();
        var items = ProjectionTargets.ListForPicker(_host.Connections, locals, prefer);
        TargetCombo.Items.Clear();
        foreach (var t in items)
            TargetCombo.Items.Add(t);

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
        _ = RefreshDisplayDefaultsAsync();
        if (!_host.IsProjectionEnvironmentRepairing)
            _ = RefreshEnvCheckAsync();
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateHints();
        _ = RefreshDisplayDefaultsAsync();
        if (!_host.IsProjectionEnvironmentRepairing)
            _ = RefreshEnvCheckAsync();
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

            if (mode == DisplayProjectionMode.Clone)
                ResolutionCombo.SelectedItem = hostItem;
            else if (peerItem != null)
                ResolutionCombo.SelectedItem = peerItem;
            else
                ResolutionCombo.SelectedIndex = 0;

            var scale = mode == DisplayProjectionMode.Clone
                ? local.ScalePercent
                : (peer?.ScalePercent > 0 ? peer.ScalePercent : local.ScalePercent);
            scale = ProjectionDisplayPresets.ClampScale(scale);
            SelectScale(scale);
        }
        finally
        {
            _suppressDisplayChange = false;
        }
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
                RefreshHz = local.RefreshHz
            };
        }

        return new ProjectionDisplayChoice
        {
            Source = res.Source,
            Width = res.Width,
            Height = res.Height,
            ScalePercent = ProjectionDisplayPresets.ClampScale(scale),
            RefreshHz = res.RefreshHz > 0 ? res.RefreshHz : 60
        };
    }

    private void UpdateDisplayHint()
    {
        if (DisplayHint == null) return;
        var mode = ReadMode();
        var choice = ReadDisplayChoice();
        if (mode == DisplayProjectionMode.Clone)
            DisplayHint.Text = $"复制：默认本机主屏。当前 {choice.ResolutionLabel} · {choice.ScaleLabel}（不改主屏，仅影响推流偏好）。";
        else if (choice.Source == ProjectionDisplaySource.TargetPeer)
            DisplayHint.Text = $"扩展：副屏将设为 {choice.ResolutionLabel}（目标电脑），主屏分辨率与缩放不变。";
        else
            DisplayHint.Text = $"扩展：副屏将设为 {choice.ResolutionLabel} · {choice.ScaleLabel}，主屏不变。";
    }

    private void UpdateHints()
    {
        if (ModeHint == null || TargetHint == null) return;
        var mode = ReadMode();
        ModeHint.Text = DisplayProjectionModeInfo.Description(mode);

        var t = ReadTarget();
        if (t == null)
            TargetHint.Text = "暂无目标。请连接电脑，或接入副屏后刷新。";
        else if (t.Kind == ProjectionTargetKind.LocalSecondaryMonitor)
            TargetHint.Text = "将采集本机副屏画面，并推送到一台双向已连接电脑。";
        else if (!_host.Connections.GetPeerStatus(t.Id).Bidirectional)
            TargetHint.Text = $"「{t.DisplayName}」尚未双向连接。";
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

    private async Task RefreshUninstallPanelAsync()
    {
        if (UninstallPanel == null) return;
        // 仅本程序安装且驱动管道已就绪（环境检查可通过）时显示卸载
        var show = false;
        if (_host.IsMttInstalledByApp
            && !_host.IsProjectionEnvironmentRepairing)
        {
            show = await MttVddEnvironment.IsDriverReadyAsync().ConfigureAwait(true);
        }

        UninstallPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (UninstallStatus != null && !show)
            UninstallStatus.Text = "";
    }

    private void RefreshUninstallPanel()
        => _ = RefreshUninstallPanelAsync();

    private void UpdateStartEnabled()
    {
        if (StartBtn == null) return;
        StartBtn.IsEnabled = !_busy && _envReady && !_host.IsProjectionEnvironmentRepairing;
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
        UpdateRepairActionButtons();
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

        var (ready, detail) = await MttVddEnvironment.CheckExtendReadyAsync().ConfigureAwait(true);
        if (seq != _envSeq) return;
        if (ready)
        {
            SetEnvUi(detail + "。环境检查已通过。", true, false);
            return;
        }

        SetEnvUi(
            detail + "。可自动安装 Virtual Display Driver（优先离线包 VddOffline，其次 winget）。",
            false,
            showRepair: true);
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
        try
        {
            var result = await _host.StartProjectionAsync(
                ReadTarget(), ReadMode(), ReadDisplayChoice(), AudioSyncCheck.IsChecked == true)
                .ConfigureAwait(true);
            if (result.NeedEnvironmentRepair)
            {
                SetEnvUi(result.Message ?? "需要安装虚拟显示驱动", false, true, "请点击「自动修复环境」");
                return;
            }

            if (!result.Ok)
            {
                MessageBox.Show(this, result.Message ?? "投影失败", "投影",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
