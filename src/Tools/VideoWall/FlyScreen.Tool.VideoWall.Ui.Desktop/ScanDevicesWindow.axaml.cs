using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public partial class ScanDevicesWindow : Window
{
    private readonly IVideoWallService _service = null!;
    private readonly ObservableCollection<ScanRowVm> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _scanning;

    public ScanDevicesWindow() => InitializeComponent();

    public ScanDevicesWindow(IVideoWallService service) : this()
    {
        _service = service;
        DeviceList.ItemsSource = _rows;
        VendorCombo.Items.Add("海康");
        VendorCombo.Items.Add("大华");
        VendorCombo.Items.Add("宇视");
        VendorCombo.Items.Add("Axis");
        VendorCombo.SelectedIndex = 0;
        ApplyLoc();
        BindSubnetChoices();
        Opened += async (_, _) => await StartScanAsync();
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
        };
    }

    private void BindSubnetChoices()
    {
        var cidrs = LanDeviceScanService.ListLocalCidrs();
        SubnetCombo.Items.Clear();
        foreach (var cidr in cidrs)
            SubnetCombo.Items.Add(cidr);
        SubnetCombo.SelectedIndex = 0;
        SubnetCombo.Text = cidrs[0];
    }

    private void ApplyLoc()
    {
        Title = Loc.T("Tool.VideoWall.Scan.Title");
        TitleText.Text = Loc.T("Tool.VideoWall.Scan.Title");
        SubnetLabel.Text = Loc.T("Tool.VideoWall.Scan.Subnet");
        ToolTip.SetTip(SubnetCombo, Loc.T("Tool.VideoWall.Scan.SubnetHint"));
        VendorLabel.Text = Loc.T("Tool.VideoWall.Scan.Vendor");
        UserLabel.Text = Loc.T("Tool.VideoWall.Scan.Username");
        PassLabel.Text = Loc.T("Tool.VideoWall.Scan.Password");
        PassBox.ShowPasswordTip = Loc.T("Tool.VideoWall.ShowPassword");
        PassBox.HidePasswordTip = Loc.T("Tool.VideoWall.HidePassword");
        RescanBtn.Content = Loc.T("Tool.VideoWall.Scan.Rescan");
        CloseBtn.Content = Loc.T("Tool.VideoWall.Scan.Close");
        HdrHost.Text = Loc.T("Tool.VideoWall.Scan.Host");
        HdrKind.Text = Loc.T("Tool.VideoWall.Scan.Kind");
        HdrPorts.Text = Loc.T("Tool.VideoWall.Scan.Ports");
        HdrActions.Text = Loc.T("Tool.VideoWall.Scan.Actions");
        StatusText.Text = Loc.T("Tool.VideoWall.Scan.CredHint");
    }

    private string CurrentSubnet()
        => (SubnetCombo.Text ?? SubnetCombo.SelectedItem as string ?? "").Trim();

    private async void RescanBtn_Click(object? sender, RoutedEventArgs e)
        => await StartScanAsync();

    private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        => Close();

    private string VendorId => VendorCombo.SelectedIndex switch
    {
        1 => "dahua",
        2 => "uniview",
        3 => "axis",
        _ => "hikvision"
    };

    private async Task StartScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        RescanBtn.IsEnabled = false;
        _rows.Clear();
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var progress = new Progress<LanScanProgress>(p =>
                StatusText.Text = string.Format(
                    Loc.T("Tool.VideoWall.Scan.Scanning"), p.Done, p.Total, p.Found));

            await foreach (var device in new LanDeviceScanService()
                               .ScanAsync(CurrentSubnet(), progress, ct)
                               .ConfigureAwait(true))
            {
                _rows.Add(new ScanRowVm(device, LanDeviceScanService.IsAlreadyAdded(_service, device)));
            }

            StatusText.Text = _rows.Count == 0
                ? Loc.T("Tool.VideoWall.Scan.Empty")
                : string.Format(Loc.T("Tool.VideoWall.Scan.Done"), _rows.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("Tool.VideoWall.Scan.Empty");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _scanning = false;
            RescanBtn.IsEnabled = true;
        }
    }

    private async void AddDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScanRowVm row } || !row.CanAdd) return;
        try
        {
            var descriptor = LanDeviceScanService.BuildCameraSource(
                row.Device, _service.Credentials, UserBox.Text, PassBox.Password, VendorId);
            _service.Credentials.Save();
            var result = await _service.AddSourceAsync(descriptor).ConfigureAwait(true);
            if (!result.Added)
            {
                StatusText.Text = string.Format(Loc.T("Tool.VideoWall.Scan.AddFail"), result.Reason);
                if (result.Existing != null) row.MarkAdded();
                return;
            }

            await _service.RefreshCoverAsync(descriptor).ConfigureAwait(true);
            row.MarkAdded();
            StatusText.Text = string.Format(Loc.T("Tool.VideoWall.Scan.AddOk"), descriptor.Name);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Loc.T("Tool.VideoWall.Scan.AddFail"), ex.Message);
        }
    }

    private sealed class ScanRowVm : INotifyPropertyChanged
    {
        private bool _added;
        public ScanRowVm(ScannedLanDevice device, bool added)
        {
            Device = device;
            _added = added;
        }

        public ScannedLanDevice Device { get; }
        public string Host => Device.Host;
        public string KindHint => Device.KindHint;
        public string PortsText => string.Join(", ", Device.OpenPorts);
        public bool CanAdd => !_added;
        public string ActionLabel => _added
            ? Loc.T("Tool.VideoWall.Scan.Added")
            : Loc.T("Tool.VideoWall.Scan.Add");

        public void MarkAdded()
        {
            _added = true;
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(ActionLabel));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
