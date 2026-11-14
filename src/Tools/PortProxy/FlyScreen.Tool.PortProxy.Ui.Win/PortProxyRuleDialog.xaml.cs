using System.Windows;
using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Win;

public partial class PortProxyRuleDialog : Window
{
    public PortProxyRuleInfo? Result { get; private set; }

    public PortProxyRuleDialog(bool isEdit, PortProxyRuleInfo seed)
    {
        InitializeComponent();
        Title = Loc.T(isEdit ? "Tool.PortProxy.DialogEdit" : "Tool.PortProxy.DialogAdd");
        TitleText.Text = Title;
        ApplyLoc();

        KindCombo.ItemsSource = new[] { "v4tov4", "v4tov6", "v6tov4", "v6tov6" };
        KindCombo.SelectedItem = seed.KindToken;
        KindCombo.IsEnabled = !isEdit;

        ListenAddressBox.Text = seed.ListenAddress;
        ListenPortBox.Text = seed.ListenPort;
        ConnectAddressBox.Text = seed.ConnectAddress;
        ConnectPortBox.Text = seed.ConnectPort;
        ProtocolBox.Text = string.IsNullOrWhiteSpace(seed.Protocol) ? "tcp" : seed.Protocol;

        if (isEdit)
        {
            ListenAddressBox.IsReadOnly = true;
            ListenPortBox.IsReadOnly = true;
        }
    }

    private void ApplyLoc()
    {
        KindLabel.Text = Loc.T("Tool.PortProxy.Kind");
        ListenAddressLabel.Text = Loc.T("Tool.PortProxy.ListenAddress");
        ListenPortLabel.Text = Loc.T("Tool.PortProxy.ListenPort");
        ConnectAddressLabel.Text = Loc.T("Tool.PortProxy.ConnectAddress");
        ConnectPortLabel.Text = Loc.T("Tool.PortProxy.ConnectPort");
        ProtocolLabel.Text = Loc.T("Tool.PortProxy.Protocol");
        OkBtn.Content = Loc.T("Tool.PortProxy.Ok");
        CancelBtn.Content = Loc.T("Tool.PortProxy.Cancel");
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        var listenPort = ListenPortBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(listenPort)
            || !int.TryParse(listenPort, out var p)
            || p is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show(this, Loc.T("Tool.PortProxy.ValidationPort"),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var connectAddress = ConnectAddressBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(connectAddress))
        {
            System.Windows.MessageBox.Show(this, Loc.T("Tool.PortProxy.ValidationConnect"),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var token = KindCombo.SelectedItem as string ?? "v4tov4";
        var kind = token.ToLowerInvariant() switch
        {
            "v4tov6" => PortProxyMappingKind.V4ToV6,
            "v6tov4" => PortProxyMappingKind.V6ToV4,
            "v6tov6" => PortProxyMappingKind.V6ToV6,
            _ => PortProxyMappingKind.V4ToV4
        };

        var connectPort = ConnectPortBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(connectPort))
            connectPort = listenPort;

        Result = new PortProxyRuleInfo
        {
            Kind = kind,
            ListenAddress = ListenAddressBox.Text?.Trim() ?? "",
            ListenPort = listenPort,
            ConnectAddress = connectAddress,
            ConnectPort = connectPort,
            Protocol = string.IsNullOrWhiteSpace(ProtocolBox.Text) ? "tcp" : ProtocolBox.Text.Trim()
        };
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
