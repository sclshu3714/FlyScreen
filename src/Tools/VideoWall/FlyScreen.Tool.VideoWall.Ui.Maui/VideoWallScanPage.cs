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
        // Windows：Picker.Title → ComboBox.Header，会与下方 Field 标签重复并挤压选择框；
        // Android：Title 作原生选择器对话框标题，保留。
        var subnetTitle = OperatingSystem.IsWindows() ? null : Loc.T("Tool.VideoWall.Scan.Subnet");
        var vendorTitle = OperatingSystem.IsWindows() ? null : Loc.T("Tool.VideoWall.Scan.Vendor");
        _subnet = new Picker
        {
            Title = subnetTitle,
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
            Title = vendorTitle,
            TextColor = text,
            ItemsSource = new[] { "海康", "大华", "宇视", "Axis" },
            SelectedIndex = 0,
            HeightRequest = 40
        };
        _status = new Label
        {
            TextColor = muted,
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
            HorizontalTextAlignment = TextAlignment.End,
            MaxLines = 1
        };
        _rescan = new Button
        {
            Text = Loc.T("Tool.VideoWall.Scan.Rescan"),
            Style = TryStyle("DlgPrimaryBtn"),
            BackgroundColor = blue,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center
        };
        _rescan.Clicked += async (_, _) => await StartScanAsync();

        ToolTipProperties.SetText(_subnet, Loc.T("Tool.VideoWall.Scan.SubnetHint"));
        ToolTipProperties.SetText(_vendor, Loc.T("Tool.VideoWall.Scan.Vendor"));
        _user.Placeholder = Loc.T("Tool.VideoWall.Scan.Username");
        _pass.Placeholder = Loc.T("Tool.VideoWall.Scan.Password");

        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            VerticalOptions = LayoutOptions.Fill,
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

        // 单行工具条：网段 / 厂商 / 用户名 / 密码 / 扫描
        Grid.SetColumn(_vendor, 1);
        Grid.SetColumn(_user, 2);
        Grid.SetColumn(_pass, 3);
        Grid.SetColumn(_rescan, 4);
        var toolbar = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(100)),
                new ColumnDefinition(new GridLength(0.8, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(0.8, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 8,
            Children = { _subnet, _vendor, _user, _pass, _rescan }
        };

        var listBorder = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = listBg,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = 4,
            VerticalOptions = LayoutOptions.Fill,
            Content = list
        };

        var body = new Grid
        {
            Padding = new Thickness(16, 12),
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            ],
            RowSpacing = 10,
            Children = { toolbar, listBorder }
        };
        Grid.SetRow(listBorder, 1);

        var shell = new MauiDialogShell
        {
            Title = Loc.T("Tool.VideoWall.Scan.Title"),
            ShowClose = true,
            // 固定宽高（对齐 Win/Desktop 720×520），避免 Wide 按内容收矮
            SizeMode = MauiDialogSize.Fixed,
            IdealWidth = 720,
            IdealHeight = 520,
            // 进度放在标题栏关闭按钮左侧
            TitleAccessory = _status,
            Body = body,
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
