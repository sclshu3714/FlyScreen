using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.SharedDevices;

namespace FlyScreen.DesktopUi;

public partial class SharedDevicesIncomingWindow : Window
{
    private readonly SharedDevicesService _sd;
    private readonly Dictionary<string, ComboBox> _combos = new(StringComparer.OrdinalIgnoreCase);

    public bool Accepted { get; private set; }
    public bool Remember => RememberCheck.IsChecked == true;
    public IReadOnlyDictionary<string, string> SelectedDevices { get; private set; }
        = new Dictionary<string, string>();

    public SharedDevicesIncomingWindow(SharedDevicesService sd, SharedDevicesIncomingRequest req)
    {
        _sd = sd;
        InitializeComponent();
        Title = Loc.T("Tool.SharedDevices.IncomingTitle");
        var kinds = string.Join("、", req.Kinds.Select(ToolCardRows.LocalizeKind));
        BodyText.Text = string.Format(Loc.T("Tool.SharedDevices.IncomingBody"), req.FromPeer, kinds);
        RememberCheck.Content = Loc.T("Tool.SharedDevices.Remember");
        RememberHint.Text = Loc.T("Tool.SharedDevices.RememberHint");
        RememberCheck.IsChecked = false;
        AcceptBtn.Content = Loc.T("Tool.SharedDevices.Accept");
        RejectBtn.Content = Loc.T("Tool.SharedDevices.Reject");

        foreach (var kind in req.Kinds)
        {
            DevicePanel.Children.Add(new TextBlock { Text = ToolCardRows.LocalizeKind(kind) });
            var combo = new ComboBox { MinHeight = 30 };
            Fill(combo, kind);
            DevicePanel.Children.Add(combo);
            _combos[kind] = combo;
        }
    }

    private void Fill(ComboBox combo, string kind)
    {
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

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, combo) in _combos)
        {
            if (combo.SelectedItem is DeviceItem item && !string.IsNullOrEmpty(item.Id))
                map[kind] = item.Id;
        }

        SelectedDevices = map;
        Accepted = true;
        Close(true);
    }

    private void Reject_Click(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close(false);
    }

    private sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
