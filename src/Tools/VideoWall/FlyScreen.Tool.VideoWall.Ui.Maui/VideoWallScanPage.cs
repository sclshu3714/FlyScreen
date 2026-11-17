using System.Collections.ObjectModel;
using System.ComponentModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>局域网扫描：主窗 PushModal + MauiDialogShell（壳外全透明）。</summary>
public sealed class VideoWallScanPage : ContentPage
{
    private readonly IVideoWallService _service;
    private readonly ObservableCollection<ScanRow> _rows = new();
    private readonly Picker _subnet;
    private readonly Entry _user;
    private readonly SecurePasswordBox _pass;
    private readonly Picker _vendor;
    private readonly Label _status;
    private readonly Button _rescan;
    private CancellationTokenSource? _cts;
    private bool _scanning;
    private bool _closing;

    public event Action? SourcesChanged;

    public VideoWallScanPage(IVideoWallService service)
    {
        _service = service;
        MauiDialogShell.PrepareOverlayPage(this);
        Title = Loc.T("Tool.VideoWall.Scan.Title");

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Color.FromArgb("#6B7280");
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var listBg = ColorOf("ListBg") ?? Color.FromArgb("#F9FAFB");
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");

        var cidrs = LanDeviceScanService.ListLocalCidrs();
        _subnet = new Picker
        {
            Title = Loc.T("Tool.VideoWall.Scan.Subnet"),
            TextColor = text,
            ItemsSource = cidrs.ToList(),
            SelectedIndex = 0,
            HeightRequest = 40
        };
        _user = new Entry
        {
            TextColor = text,
            Placeholder = Loc.T("Tool.VideoWall.Scan.Username"),
            PlaceholderColor = muted,
            HeightRequest = 40
        };
        _pass = new SecurePasswordBox
        {
            Placeholder = Loc.T("Tool.VideoWall.Scan.Password"),
            ShowPasswordTip = Loc.T("Tool.VideoWall.ShowPassword"),
            HidePasswordTip = Loc.T("Tool.VideoWall.HidePassword")
        };
        _pass.ApplyTheme(text, muted, line, card);
        _vendor = new Picker
        {
            Title = Loc.T("Tool.VideoWall.Scan.Vendor"),
            TextColor = text,
            ItemsSource = new[] { "海康", "大华", "宇视", "Axis" },
            SelectedIndex = 0,
            HeightRequest = 40
        };
        _status = new Label { TextColor = muted, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
        _rescan = new Button
        {
            Text = Loc.T("Tool.VideoWall.Scan.Rescan"),
            Style = TryStyle("DlgPrimaryBtn"),
            BackgroundColor = blue,
            TextColor = Colors.White
        };
        _rescan.Clicked += async (_, _) => await StartScanAsync();

        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var host = new Label { FontSize = 13, TextColor = text };
                host.SetBinding(Label.TextProperty, nameof(ScanRow.Host));
                var kind = new Label { FontSize = 12, TextColor = muted };
                kind.SetBinding(Label.TextProperty, nameof(ScanRow.KindHint));
                var ports = new Label { FontSize = 12, TextColor = muted, LineBreakMode = LineBreakMode.TailTruncation };
                ports.SetBinding(Label.TextProperty, nameof(ScanRow.PortsText));
                var btn = new Button
                {
                    FontSize = 12,
                    CornerRadius = 8,
                    Padding = new Thickness(10, 6),
                    BackgroundColor = blue,
                    TextColor = Colors.White
                };
                btn.SetBinding(Button.TextProperty, nameof(ScanRow.ActionLabel));
                btn.SetBinding(VisualElement.IsEnabledProperty, nameof(ScanRow.CanAdd));
                btn.Clicked += async (s, _) =>
                {
                    if ((s as BindableObject)?.BindingContext is ScanRow row)
                        await AddAsync(row);
                };

                Grid.SetColumn(kind, 1);
                Grid.SetColumn(ports, 2);
                Grid.SetColumn(btn, 3);
                return new Grid
                {
                    Padding = new Thickness(8, 6),
                    ColumnDefinitions =
                    [
                        new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                        new ColumnDefinition(new GridLength(0.8, GridUnitType.Star)),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(110))
                    ],
                    ColumnSpacing = 6,
                    Children = { host, kind, ports, btn }
                };
            })
        };

        var closeBtn = new Button
        {
            Text = Loc.T("Tool.VideoWall.Scan.Close"),
            Style = TryStyle("DlgNormalBtn")
        };
        closeBtn.Clicked += async (_, _) => await CloseAsync();

        var subnetCol = Field(Loc.T("Tool.VideoWall.Scan.Subnet"), muted, _subnet);
        var vendorCol = Field(Loc.T("Tool.VideoWall.Scan.Vendor"), muted, _vendor);
        Grid.SetColumn(vendorCol, 1);
        _rescan.VerticalOptions = LayoutOptions.End;
        Grid.SetColumn(_rescan, 2);
        var topRow = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(120)),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 8,
            Children = { subnetCol, vendorCol, _rescan }
        };

        var userCol = Field(Loc.T("Tool.VideoWall.Scan.Username"), muted, _user);
        var passCol = Field(Loc.T("Tool.VideoWall.Scan.Password"), muted, _pass);
        Grid.SetColumn(passCol, 1);
        var credRow = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
            Children = { userCol, passCol }
        };

        var listBorder = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = listBg,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = 4,
            MinimumHeightRequest = 160,
            Content = list
        };

        var shell = new MauiDialogShell
        {
            Title = Loc.T("Tool.VideoWall.Scan.Title"),
            ShowClose = true,
            SizeMode = MauiDialogSize.Wide,
            Body = new ScrollView
            {
                Padding = new Thickness(16, 12),
                Content = new VerticalStackLayout
                {
                    Spacing = 10,
                    Children = { topRow, credRow, listBorder, _status }
                }
            },
            Footer = new HorizontalStackLayout
            {
                HorizontalOptions = LayoutOptions.End,
                Children = { closeBtn }
            }
        };
        shell.CloseRequested += async (_, _) => await CloseAsync();
        Content = shell;

        Appearing += (_, _) => _ = StartScanAsync();
    }

    public static async Task ShowAsync(IVideoWallService service, Action? onSourcesChanged = null)
    {
        var page = new VideoWallScanPage(service);
        if (onSourcesChanged != null)
            page.SourcesChanged += onSourcesChanged;
        var nav = ResolveNavigation();
        if (nav == null) return;
        await nav.PushModalAsync(page, animated: true);
    }

    private static INavigation? ResolveNavigation()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return null;
        if (page is NavigationPage nav) return nav.Navigation;
        return page.Navigation;
    }

    private async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    private string VendorId => _vendor.SelectedIndex switch
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
        _rescan.IsEnabled = false;
        _rows.Clear();
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var progress = new Progress<LanScanProgress>(p =>
                _status.Text = string.Format(Loc.T("Tool.VideoWall.Scan.Scanning"), p.Done, p.Total, p.Found));

            var subnet = _subnet.SelectedItem as string ?? "";
            await foreach (var device in new LanDeviceScanService()
                               .ScanAsync(subnet, progress, ct)
                               .ConfigureAwait(true))
            {
                var added = LanDeviceScanService.IsAlreadyAdded(_service, device);
                _rows.Add(new ScanRow(device, added));
            }

            _status.Text = _rows.Count == 0
                ? Loc.T("Tool.VideoWall.Scan.Empty")
                : string.Format(Loc.T("Tool.VideoWall.Scan.Done"), _rows.Count);
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.T("Tool.VideoWall.Scan.Empty");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _scanning = false;
            _rescan.IsEnabled = true;
        }
    }

    private async Task AddAsync(ScanRow row)
    {
        if (!row.CanAdd) return;
        try
        {
            var descriptor = LanDeviceScanService.BuildCameraSource(
                row.Device, _service.Credentials, _user.Text, _pass.Password, VendorId);
            _service.Credentials.Save();
            var result = await _service.AddSourceAsync(descriptor).ConfigureAwait(true);
            if (!result.Added)
            {
                _status.Text = string.Format(Loc.T("Tool.VideoWall.Scan.AddFail"), result.Reason);
                if (result.Existing != null) row.MarkAdded();
                return;
            }

            _ = await _service.RefreshCoverAsync(descriptor).ConfigureAwait(true);
            row.MarkAdded();
            _status.Text = string.Format(Loc.T("Tool.VideoWall.Scan.AddOk"), descriptor.Name);
            SourcesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Loc.T("Tool.VideoWall.Scan.AddFail"), ex.Message);
        }
    }

    private static View Field(string label, Color muted, View editor)
        => new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = label, FontSize = 12, TextColor = muted },
                editor
            }
        };

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

    private sealed class ScanRow : INotifyPropertyChanged
    {
        private bool _added;
        public ScanRow(ScannedLanDevice device, bool added)
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAdd)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionLabel)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
