using System.Windows;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace FlyScreen.Tool.SharedDevices.Ui.Win;

public partial class SharedDevicesIncomingDialog : Window
{
    private readonly SharedDevicesService _sd;
    private readonly SharedDevicesIncomingRequest _req;
    private readonly Dictionary<string, ComboBox> _combos = new(StringComparer.OrdinalIgnoreCase);

    public bool Accepted { get; private set; }
    public bool Remember => RememberCheck.IsChecked == true;
    public IReadOnlyDictionary<string, string> SelectedDevices { get; private set; }
        = new Dictionary<string, string>();

    public SharedDevicesIncomingDialog(SharedDevicesService sd, SharedDevicesIncomingRequest req)
    {
        _sd = sd;
        _req = req;
        InitializeComponent();
        Title = Loc.T("Tool.SharedDevices.IncomingTitle");
        var kinds = string.Join("、", req.Kinds.Select(SharedDevicesKindLabels.Localize));
        BodyText.Text = string.Format(Loc.T("Tool.SharedDevices.IncomingBody"), req.FromPeer, kinds);
        RememberCheck.Content = Loc.T("Tool.SharedDevices.Remember");
        RememberHint.Text = Loc.T("Tool.SharedDevices.RememberHint");
        RememberCheck.IsChecked = false;
        AcceptBtn.Content = Loc.T("Tool.SharedDevices.Accept");
        RejectBtn.Content = Loc.T("Tool.SharedDevices.Reject");

        var anyDevice = false;
        foreach (var kind in req.Kinds)
        {
            var list = _sd.EnumerateLocalDevices(kind);
            if (list.Count == 0)
                continue;

            anyDevice = true;
            var label = new TextBlock
            {
                Text = SharedDevicesKindLabels.Localize(kind),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var combo = new ComboBox { Height = 30, Margin = new Thickness(0, 0, 0, 10) };
            FillCombo(combo, kind, list);
            DevicePanel.Children.Add(label);
            DevicePanel.Children.Add(combo);
            _combos[kind] = combo;
        }

        AcceptBtn.IsEnabled = anyDevice;
        if (!anyDevice)
            BodyText.Text = string.Format(Loc.T("Tool.SharedDevices.IncomingBody"), req.FromPeer,
                Loc.T("Tool.SharedDevices.PeerNoDevice"));
    }

    private void FillCombo(ComboBox combo, string kind, IReadOnlyList<FlyScreen.ToolSdk.SharedLocalDevice> list)
    {
        var selected = _sd.GetSelectedDeviceId(kind);
        DeviceItem? pick = null;
        foreach (var d in list)
        {
            var item = new DeviceItem(d.Id, d.Name);
            combo.Items.Add(item);
            if (d.Id == selected) pick = item;
        }

        combo.SelectedItem = pick ?? combo.Items[0];
        combo.DisplayMemberPath = nameof(DeviceItem.Name);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_combos.Count == 0)
        {
            Accepted = false;
            DialogResult = false;
            Close();
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, combo) in _combos)
        {
            if (combo.SelectedItem is DeviceItem item && !string.IsNullOrEmpty(item.Id))
                map[kind] = item.Id;
        }

        if (map.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Tool.SharedDevices.NoLocalDevice"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedDevices = map;
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
        Close();
    }

    private sealed record DeviceItem(string Id, string Name);
}

