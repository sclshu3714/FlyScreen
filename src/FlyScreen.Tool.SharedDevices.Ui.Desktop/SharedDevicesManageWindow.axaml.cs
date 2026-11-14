using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.SharedDevices;

namespace FlyScreen.Tool.SharedDevices.Ui.Desktop;

public partial class SharedDevicesManageWindow : Window
{
    private readonly SharedDevicesService _sd;

    public SharedDevicesManageWindow(SharedDevicesService sd)
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
        Fill(SpeakerCombo, SharedDeviceKinds.Speaker);
        Fill(MicCombo, SharedDeviceKinds.Microphone);
        Fill(CameraCombo, SharedDeviceKinds.Camera);
    }

    private void Fill(ComboBox combo, string kind)
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
            var item = new DeviceItem(d.Id, d.Name);
            combo.Items.Add(item);
            if (d.Id == selected) pick = item;
        }

        combo.SelectedItem = pick ?? combo.Items[0];
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Apply(SpeakerCombo, SharedDeviceKinds.Speaker);
        Apply(MicCombo, SharedDeviceKinds.Microphone);
        Apply(CameraCombo, SharedDeviceKinds.Camera);
        Close(true);
    }

    private void Apply(ComboBox combo, string kind)
    {
        if (combo.SelectedItem is DeviceItem item && !string.IsNullOrEmpty(item.Id))
            _sd.SetSelectedDevice(kind, item.Id);
    }

    private void ClearRemember_Click(object? sender, RoutedEventArgs e)
    {
        _sd.ClearRemember();
        ClearStatus.Text = Loc.T("Tool.SharedDevices.ClearRememberDone");
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);

    private sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}

