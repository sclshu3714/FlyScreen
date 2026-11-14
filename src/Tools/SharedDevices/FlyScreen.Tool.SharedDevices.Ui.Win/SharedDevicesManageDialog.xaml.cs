using System.Windows;
using System.Windows.Markup;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.SharedDevices;
using FlyScreen.ToolSdk;
using ComboBox = System.Windows.Controls.ComboBox;

namespace FlyScreen.Tool.SharedDevices.Ui.Win;

public partial class SharedDevicesManageDialog : Window
{
    private static readonly DataTemplate DeviceNameTemplate = CreateDeviceNameTemplate();

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
        // WPF 禁止同时设 DisplayMemberPath + ItemTemplate；只用 ItemTemplate 显示 Name。
        combo.ItemTemplate = DeviceNameTemplate;

        var list = _sd.EnumerateLocalDevices(kind);
        if (list.Count == 0)
        {
            combo.Items.Add(new SharedLocalDevice
            {
                Kind = kind,
                Id = "",
                Name = Loc.T("Tool.SharedDevices.NoLocalDevice")
            });
            combo.SelectedIndex = 0;
            combo.IsEnabled = false;
            return;
        }

        var selected = _sd.GetSelectedDeviceId(kind);
        SharedLocalDevice? pick = null;
        foreach (var d in list)
        {
            var item = d.IsDefault
                ? new SharedLocalDevice
                {
                    Kind = d.Kind,
                    Id = d.Id,
                    Name = $"{d.Name}（{Loc.T("Tool.SharedDevices.DefaultDevice")}）",
                    IsDefault = true
                }
                : d;
            combo.Items.Add(item);
            if (d.Id == selected) pick = item;
        }

        combo.SelectedItem = pick ?? combo.Items[0];
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
        if (combo.SelectedItem is SharedLocalDevice d && !string.IsNullOrEmpty(d.Id))
            _sd.SetSelectedDevice(kind, d.Id);
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

    private static DataTemplate CreateDeviceNameTemplate()
    {
        const string xaml =
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
            </DataTemplate>
            """;
        return (DataTemplate)XamlReader.Parse(xaml);
    }
}
