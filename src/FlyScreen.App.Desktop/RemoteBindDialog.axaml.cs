using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Network;

namespace FlyScreen.DesktopUi;

/// <summary>将远程/中转设备投影到电脑布局中的某台电脑。</summary>
public partial class RemoteBindDialog : Window
{
    public string? SelectedDeviceId { get; private set; }
    private bool _accepted;

    public RemoteBindDialog()
    {
        InitializeComponent();
    }

    public static async Task<string?> ShowAsync(
        Window owner,
        string remoteName,
        IReadOnlyList<RemoteBindingHelper.LayoutTarget> targets,
        string? currentDeviceId = null)
    {
        if (targets.Count == 0) return null;

        var dlg = new RemoteBindDialog
        {
            Title = Loc.T("Net.BindTitle")
        };
        dlg.TitleText.Text = Loc.T("Net.BindTitle");
        dlg.HintText.Text = string.Format(Loc.T("Net.BindHint"), remoteName);
        dlg.TargetLabel.Text = Loc.T("Net.BindTarget");
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

        await dlg.ShowDialog(owner).ConfigureAwait(true);
        return dlg._accepted ? dlg.SelectedDeviceId : null;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetCombo.SelectedItem is not RemoteBindingHelper.LayoutTarget t)
            return;
        SelectedDeviceId = t.DeviceId;
        _accepted = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }
}
