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

    public ProjectionDialog() : this(null!) { }

    public ProjectionDialog(IDesktopShellHost host)
    {
        _host = host;
        InitializeComponent();
        TryApplyWindowIcon();
        DesktopThemeService.Apply(DesktopThemeService.Current, this);
        StartBtn.IsEnabled = false;
        Opened += (_, _) =>
        {
            if (_host == null) return;
            DesktopThemeService.Apply(DesktopThemeService.Current, this);
            ModeCombo.Items.Clear();
            foreach (var m in DisplayProjectionModeInfo.PickerModes)
                ModeCombo.Items.Add(DisplayProjectionModeInfo.Label(m));
            var prefer = DisplayProjectionModeInfo.ClampForPicker(_host.Settings.ScreenExtendProjectionMode);
            ModeCombo.SelectedIndex = prefer == DisplayProjectionMode.Clone ? 0 : 1;
            ScaleCombo.Items.Clear();
            foreach (var s in ProjectionDisplayPresets.ScalePercents)
                ScaleCombo.Items.Add($"{s}%");
            ScaleCombo.SelectedIndex = 0;
            RefreshTargets();
            RefreshDisplayPresets();
            UpdateHints();
            RefreshEnvCheck();
        };
    }

    private void RefreshDisplayPresets()
    {
        ResolutionCombo.Items.Clear();
        ResolutionCombo.Items.Add("本机主屏（自动）");
        ResolutionCombo.Items.Add("目标电脑（自动）");
        foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
            ResolutionCombo.Items.Add($"{w}×{h}");
        ResolutionCombo.SelectedIndex = ReadMode() == DisplayProjectionMode.Clone ? 0 : 1;
        DisplayHint.Text = "Mac/Linux 本期不发起投影；分辨率选项与 Windows 对齐，供接收端应答查询。";
    }

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
        var prefer = _host.Settings.ScreenExtendPeer;
        var items = ProjectionTargets.ListForPicker(_host.Connections, null, prefer);
        TargetCombo.Items.Clear();
        foreach (var t in items)
            TargetCombo.Items.Add(t);

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
        RefreshDisplayPresets();
        RefreshEnvCheck();
    }

    private void Target_Changed(object? sender, SelectionChangedEventArgs e)
    {
        UpdateHints();
        RefreshEnvCheck();
    }

    private void UpdateHints()
    {
        if (ModeHint == null || TargetHint == null || _host == null) return;
        var mode = ReadMode();
        ModeHint.Text = DisplayProjectionModeInfo.Description(mode);

        var t = ReadTarget();
        if (t == null)
            TargetHint.Text = "暂无双向已连接电脑。";
        else if (mode == DisplayProjectionMode.Extend)
            TargetHint.Text = "Mac/Linux 本期不发起扩展投影；请在 Windows 主机操作。";
        else
            TargetHint.Text = "Mac/Linux 本期不发起复制投影；可接收来自 Windows 的投影。";
    }

    private void UpdateStartEnabled()
    {
        if (StartBtn == null) return;
        StartBtn.IsEnabled = !_busy && _envReady;
        StartBtn.Opacity = StartBtn.IsEnabled ? 1 : 0.5;
    }

    private void SetEnvUi(string hint, bool ready, string? status = null)
    {
        _envReady = ready;
        EnvHint.Text = hint;
        EnvStatus.Text = status ?? "";
        UpdateStartEnabled();
    }

    private void RefreshEnvCheck()
    {
        if (EnvPanel == null || _host == null) return;
        var seq = ++_envSeq;
        SetEnvUi("正在检查环境…", ready: false);

        var mode = ReadMode();
        var t = ReadTarget();

        if (t == null)
        {
            if (seq != _envSeq) return;
            SetEnvUi("请选择投影目标（双向已连接电脑）。", false);
            return;
        }

        if (t.Kind == ProjectionTargetKind.Peer
            && !_host.Connections.GetPeerStatus(t.Id).Bidirectional)
        {
            if (seq != _envSeq) return;
            SetEnvUi($"「{t.DisplayName}」尚未双向连接，无法投影。", false);
            return;
        }

        // Mac/Linux 本期不发起：任意方式环境检查均不通过
        if (seq != _envSeq) return;
        if (mode == DisplayProjectionMode.Extend)
            SetEnvUi("扩展投影需 Windows 主机与虚拟显示驱动；本端本期不发起投影。", false);
        else
            SetEnvUi("复制投影请在 Windows 主机发起；本端可接收来自 Windows 的投影。", false);
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || !_envReady) return;
        _busy = true;
        UpdateStartEnabled();
        try
        {
            var (ok, msg) = await _host.StartProjectionAsync(
                ReadTarget(), ReadMode(), AudioSyncCheck.IsChecked == true);
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
