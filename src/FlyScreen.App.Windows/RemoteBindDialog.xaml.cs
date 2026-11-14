using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen;

/// <summary>将远程/中转设备投影到电脑布局中的某台电脑。</summary>
public partial class RemoteBindDialog : Window
{
    public RemoteProjectChoice? Result { get; private set; }
    private AppSettings? _settings;

    public RemoteBindDialog()
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToChildWindow(this);
    }

    /// <summary>兼容仅需目标电脑 Id 的调用（更换投影关联）。</summary>
    public static string? Show(
        Window? owner,
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId = null)
        => ShowChoice(owner, remoteName, targets, currentDeviceId, settings: null)?.DeviceId;

    public static RemoteProjectChoice? ShowChoice(
        Window? owner,
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId = null,
        AppSettings? settings = null)
    {
        if (targets.Count == 0) return null;

        var dlg = new RemoteBindDialog { _settings = settings };
        if (owner != null) dlg.Owner = owner;

        dlg.Title = Loc.T("Net.BindTitle");
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

        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void FillDisplayCombos()
    {
        ResolutionCombo.Items.Clear();
        ResolutionCombo.Items.Add(new ResItem(0, 0, ProjectionDisplayPresets.ResolutionLabel(0, 0)));
        foreach (var (w, h) in ProjectionDisplayPresets.Resolutions)
            ResolutionCombo.Items.Add(new ResItem(w, h, ProjectionDisplayPresets.ResolutionLabel(w, h)));

        var rw = _settings?.ScreenExtendWidth ?? 1920;
        var rh = _settings?.ScreenExtendHeight ?? 1080;
        SelectResolution(rw, rh);

        BitrateCombo.Items.Clear();
        foreach (var fps in ProjectionDisplayPresets.FrameRates)
            BitrateCombo.Items.Add(new NamedIntItem(fps, ProjectionDisplayPresets.BitrateLabel(fps)));
        SelectNamed(BitrateCombo, _settings != null
            ? ProjectionDisplayPresets.RememberedFps(_settings)
            : ProjectionDisplayPresets.DefaultFps);

        QualityCombo.Items.Clear();
        foreach (var q in ProjectionDisplayPresets.JpegQualities)
            QualityCombo.Items.Add(new NamedIntItem(q, ProjectionDisplayPresets.JpegQualityLabel(q)));
        SelectNamed(QualityCombo, _settings != null
            ? ProjectionDisplayPresets.RememberedJpegQuality(_settings)
            : ProjectionDisplayPresets.DefaultJpegQuality);
    }

    private void SelectResolution(int w, int h)
    {
        for (var i = 0; i < ResolutionCombo.Items.Count; i++)
        {
            if (ResolutionCombo.Items[i] is ResItem r && r.Width == w && r.Height == h)
            {
                ResolutionCombo.SelectedIndex = i;
                return;
            }
        }

        if (w > 0 && h > 0)
        {
            var custom = new ResItem(w, h, ProjectionDisplayPresets.ResolutionLabel(w, h));
            ResolutionCombo.Items.Add(custom);
            ResolutionCombo.SelectedItem = custom;
            return;
        }

        ResolutionCombo.SelectedIndex = 0;
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

    private void Ok_Click(object sender, RoutedEventArgs e)
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
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
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
