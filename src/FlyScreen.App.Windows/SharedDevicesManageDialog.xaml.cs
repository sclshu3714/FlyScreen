using System.Windows;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools.SharedDevices;
using ComboBox = System.Windows.Controls.ComboBox;

namespace FlyScreen;

public partial class SharedDevicesManageDialog : Window
{
    private readonly SharedDevicesService _sd;

    public SharedDevicesManageDialog(SharedDevicesService sd)
    {
        _sd = sd;
        InitializeComponent();
        Title = Loc.T("Tool.SharedDevices.ManageTitle");
        TitleText.Text = Loc.T("Tool.SharedDevices.ManageTitle");
        HintText.Text = Loc.T("Tool.SharedDevices.PickLocalDevice");
        SpeakerLabel.Text = Loc.T("Tool.SharedDevices.Speaker");
        MicLabel.Text = Loc.T("Tool.SharedDevices.Microphone");
        CameraLabel.Text = Loc.T("Tool.SharedDevices.Camera");
        ClearRememberBtn.Content = Loc.T("Tool.SharedDevices.ClearRemember");
        SaveBtn.Content = Loc.T("Tool.SharedDevices.Save");
        CloseBtn.Content = Loc.T("Tool.SharedDevices.Cancel");
        FillCombo(SpeakerCombo, SharedDeviceKinds.Speaker);
        FillCombo(MicCombo, SharedDeviceKinds.Microphone);
        FillCombo(CameraCombo, SharedDeviceKinds.Camera);
    }

    private void FillCombo(ComboBox combo, string kind)
    {
        combo.Items.Clear();
        var list = _sd.EnumerateLocalDevices(kind);
        if (list.Count == 0)
        {
            combo.Items.Add(new DeviceItem("", Loc.T("Tool.SharedDevices.NoLocalDevice")));
            combo.SelectedIndex = 0;
            combo.IsEnabled = false;
            return;
        }

        var selected = _sd.GetSelectedDeviceId(kind);
        DeviceItem? pick = null;
        foreach (var d in list)
        {
            var item = new DeviceItem(d.Id, d.IsDefault ? $"{d.Name}（{Loc.T("Tool.SharedDevices.DefaultDevice")}）" : d.Name);
            combo.Items.Add(item);
            if (d.Id == selected) pick = item;
        }

        combo.SelectedItem = pick ?? combo.Items[0];
        combo.DisplayMemberPath = nameof(DeviceItem.Name);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyCombo(SpeakerCombo, SharedDeviceKinds.Speaker);
        ApplyCombo(MicCombo, SharedDeviceKinds.Microphone);
        ApplyCombo(CameraCombo, SharedDeviceKinds.Camera);
        DialogResult = true;
        Close();
    }

    private void ApplyCombo(ComboBox combo, string kind)
    {
        if (combo.SelectedItem is DeviceItem item && !string.IsNullOrEmpty(item.Id))
            _sd.SetSelectedDevice(kind, item.Id);
    }

    private void ClearRemember_Click(object sender, RoutedEventArgs e)
    {
        _sd.ClearRemember();
        ClearStatus.Text = Loc.T("Tool.SharedDevices.ClearRememberDone");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed record DeviceItem(string Id, string Name);
}
