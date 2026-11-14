using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.SharedDevices;

namespace FlyScreen.Tool.SharedDevices.Ui.Win;

public partial class SharedDevicesStartDialog : Window
{
    public sealed class PeerRow
    {
        public required SharedDevicesTargetInfo Info { get; init; }
        public string Display { get; init; } = "";
        public bool Selectable { get; init; }
    }

    public string? SelectedPeer { get; private set; }
    public string[] SelectedKinds { get; private set; } = [];

    public SharedDevicesStartDialog(IReadOnlyList<SharedDevicesTargetInfo> targets)
    {
        InitializeComponent();
        Title = Loc.T("Tool.SharedDevices.StartTitle");
        TitleText.Text = Loc.T("Tool.SharedDevices.StartTitle");
        PeerLabel.Text = Loc.T("Tool.SharedDevices.PickPeer");
        KindsLabel.Text = Loc.T("Tool.SharedDevices.PickKinds");
        KindSpeaker.Content = Loc.T("Tool.SharedDevices.Speaker");
        KindMicrophone.Content = Loc.T("Tool.SharedDevices.Microphone");
        KindCamera.Content = Loc.T("Tool.SharedDevices.Camera");
        CancelBtn.Content = Loc.T("Tool.SharedDevices.Cancel");
        OkBtn.Content = Loc.T("Tool.SharedDevices.Confirm");

        var rows = new List<PeerRow>();
        foreach (var t in targets)
        {
            rows.Add(new PeerRow
            {
                Info = t,
                Display = t.FormatDisplayName(),
                Selectable = t.CanProvide
            });
        }

        PeerList.ItemsSource = rows;
        if (rows.Count == 0)
            HintText.Text = Loc.T("Tool.SharedDevices.NoTargets");
        else
        {
            var first = rows.FirstOrDefault(r => r.Selectable);
            if (first != null)
                PeerList.SelectedItem = first;
            ApplyKindsForSelection(first?.Info);
        }
    }

    private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeerList.SelectedItem is PeerRow row && !row.Selectable)
        {
            PeerList.SelectedItem = null;
            ApplyKindsForSelection(null);
            return;
        }

        ApplyKindsForSelection((PeerList.SelectedItem as PeerRow)?.Info);
    }

    private void ApplyKindsForSelection(SharedDevicesTargetInfo? info)
    {
        var kinds = info?.ProvidableKinds ?? [];
        ConfigureKind(KindSpeaker, SharedDeviceKinds.Speaker, kinds);
        ConfigureKind(KindMicrophone, SharedDeviceKinds.Microphone, kinds);
        ConfigureKind(KindCamera, SharedDeviceKinds.Camera, kinds);
    }

    private static void ConfigureKind(System.Windows.Controls.CheckBox box, string kind, IReadOnlyList<string> kinds)
    {
        var ok = kinds.Any(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase));
        box.IsEnabled = ok;
        box.IsChecked = ok;
        box.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PeerList.SelectedItem is not PeerRow row || !row.Selectable)
        {
            MessageBox.Show(this, Loc.T("Tool.SharedDevices.NoTargets"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kinds = new List<string>();
        if (KindSpeaker.IsEnabled && KindSpeaker.IsChecked == true) kinds.Add(SharedDeviceKinds.Speaker);
        if (KindMicrophone.IsEnabled && KindMicrophone.IsChecked == true) kinds.Add(SharedDeviceKinds.Microphone);
        if (KindCamera.IsEnabled && KindCamera.IsChecked == true) kinds.Add(SharedDeviceKinds.Camera);
        if (kinds.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Tool.SharedDevices.PickKinds"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPeer = row.Info.PeerName;
        SelectedKinds = kinds.ToArray();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

