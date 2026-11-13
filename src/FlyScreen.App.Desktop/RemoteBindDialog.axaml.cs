using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.DesktopUi;

/// <summary>将远程/中转设备投影到电脑布局中的某台电脑。</summary>
public partial class RemoteBindDialog : Window
{
    public RemoteProjectChoice? Result { get; private set; }
    private bool _accepted;
    private AppSettings? _settings;

    public RemoteBindDialog()
    {
        InitializeComponent();
    }

    /// <summary>兼容仅需目标电脑 Id 的调用。</summary>
    public static async Task<string?> ShowAsync(
        Window owner,
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId = null)
    {
        var choice = await ShowChoiceAsync(owner, remoteName, targets, currentDeviceId, settings: null)
            .ConfigureAwait(true);
        return choice?.DeviceId;
    }

    public static async Task<RemoteProjectChoice?> ShowChoiceAsync(
        Window owner,
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId = null,
        AppSettings? settings = null)
    {
        if (targets.Count == 0) return null;

        var dlg = new RemoteBindDialog
        {
            Title = Loc.T("Net.BindTitle"),
            _settings = settings
        };
        dlg.TitleText.Text = Loc.T("Net.BindTitle");
        dlg.HintText.Text = string.Format(Loc.T("Net.BindHint"), remoteName);
        dlg.TargetLabel.Text = Loc.T("Net.BindTarget");
        dlg.ResolutionLabel.Text = Loc.T("Net.BindResolution");
        dlg.BitrateLabel.Text = Loc.T("Net.BindBitrate");
        dlg.QualityLabel.Text = Loc.T("Net.BindQuality");
        dlg.CancelBtn.Content = Loc.T("Dlg.Cancel");
        dlg.OkBtn.Content = Loc.T("Net.BindOk");

        dlg.TargetCombo.ItemsSource = targets;
        var idx = -1;
        for (var i = 0; i < targets.Count; i++)
        {
            if (string.Equals(targets[i].DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        dlg.TargetCombo.SelectedIndex = idx >= 0 ? idx : 0;
        dlg.FillDisplayCombos();

        await dlg.ShowDialog(owner).ConfigureAwait(true);
        return dlg._accepted ? dlg.Result : null;
    }

    private void FillDisplayCombos()
    {
        var resolutions = new List<ResItem>
        {
            new(0, 0, ProjectionDisplayPresets.ResolutionLabel(0, 0))
        };
        foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
            resolutions.Add(new ResItem(w, h, ProjectionDisplayPresets.ResolutionLabel(w, h)));
        ResolutionCombo.ItemsSource = resolutions;

        var rw = _settings?.ScreenExtendWidth ?? 1920;
        var rh = _settings?.ScreenExtendHeight ?? 1080;
        var resIdx = resolutions.FindIndex(r => r.Width == rw && r.Height == rh);
        if (resIdx < 0 && rw > 0 && rh > 0)
        {
            resolutions.Add(new ResItem(rw, rh, ProjectionDisplayPresets.ResolutionLabel(rw, rh)));
            resIdx = resolutions.Count - 1;
        }
        ResolutionCombo.SelectedIndex = resIdx >= 0 ? resIdx : 0;

        var bitrates = ProjectionDisplayPresets.FrameRates
            .Select(fps => new NamedIntItem(fps, ProjectionDisplayPresets.BitrateLabel(fps)))
            .ToList();
        BitrateCombo.ItemsSource = bitrates;
        var fpsWant = _settings != null
            ? ProjectionDisplayPresets.RememberedFps(_settings)
            : ProjectionDisplayPresets.DefaultFps;
        BitrateCombo.SelectedIndex = IndexOf(bitrates, fpsWant);

        var qualities = ProjectionDisplayPresets.JpegQualities
            .Select(q => new NamedIntItem(q, ProjectionDisplayPresets.JpegQualityLabel(q)))
            .ToList();
        QualityCombo.ItemsSource = qualities;
        var qWant = _settings != null
            ? ProjectionDisplayPresets.RememberedJpegQuality(_settings)
            : ProjectionDisplayPresets.DefaultJpegQuality;
        QualityCombo.SelectedIndex = IndexOf(qualities, qWant);
    }

    private static int IndexOf(IReadOnlyList<NamedIntItem> items, int value)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i].Value == value) return i;
        return items.Count > 0 ? 0 : -1;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetCombo.SelectedItem is not RemoteBindingHelper.LayoutTarget t)
            return;
        var res = ResolutionCombo.SelectedItem as ResItem;
        var fps = BitrateCombo.SelectedItem is NamedIntItem b
            ? b.Value
            : ProjectionDisplayPresets.DefaultFps;
        var quality = QualityCombo.SelectedItem is NamedIntItem q
            ? q.Value
            : ProjectionDisplayPresets.DefaultJpegQuality;
        Result = new RemoteProjectChoice
        {
            DeviceId = t.DeviceId,
            Width = res?.Width ?? 1920,
            Height = res?.Height ?? 1080,
            Fps = fps,
            JpegQuality = quality
        };
        _accepted = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }

    private sealed record ResItem(int Width, int Height, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record NamedIntItem(int Value, string Label)
    {
        public override string ToString() => Label;
    }
}
