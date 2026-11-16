using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>Maui 居中卡片：局域网扫描并添加到源。</summary>
public sealed class VideoWallScanOverlay
{
    private readonly IVideoWallService _service;
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _card;
    private readonly Color _listBg;
    private readonly Color _blue;
    private readonly ObservableCollection<ScanRow> _rows = new();
    private readonly Picker _subnet;
    private readonly Entry _user;
    private readonly SecurePasswordBox _pass;
    private readonly Picker _vendor;
    private readonly Label _status;
    private readonly Button _rescan;
    private readonly CollectionView _list;
    private CancellationTokenSource? _cts;
    private bool _scanning;

    public Grid Host { get; }
    public event Action? Closed;
    public event Action? SourcesChanged;

    public VideoWallScanOverlay(
        IVideoWallService service,
        Color text, Color muted, Color line, Color card, Color listBg, Color blue)
    {
        _service = service;
        _text = text;
        _muted = muted;
        _line = line;
        _card = card;
        _listBg = listBg;
        _blue = blue;

        var cidrs = LanDeviceScanService.ListLocalCidrs();
        _subnet = new Picker
        {
            Title = Loc.T("Tool.VideoWall.Scan.Subnet"),
            TextColor = _text,
            ItemsSource = cidrs.ToList(),
            SelectedIndex = 0
        };
        _user = new Entry { TextColor = _text, Placeholder = Loc.T("Tool.VideoWall.Scan.Username"), PlaceholderColor = _muted };
        _pass = new SecurePasswordBox
        {
            Placeholder = Loc.T("Tool.VideoWall.Scan.Password"),
            ShowPasswordTip = Loc.T("Tool.VideoWall.ShowPassword"),
            HidePasswordTip = Loc.T("Tool.VideoWall.HidePassword")
        };
        _pass.ApplyTheme(_text, _muted, _line, _card);
        _vendor = new Picker
        {
            Title = Loc.T("Tool.VideoWall.Scan.Vendor"),
            TextColor = _text,
            ItemsSource = new[] { "海康", "大华", "宇视", "Axis" },
            SelectedIndex = 0
        };
        _status = new Label { TextColor = _muted, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
        _rescan = new Button
        {
            Text = Loc.T("Tool.VideoWall.Scan.Rescan"),
            BackgroundColor = _blue,
            TextColor = Colors.White,
            CornerRadius = 8,
            Padding = new Thickness(14, 8)
        };
        _rescan.Clicked += async (_, _) => await StartScanAsync();

        _list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var host = new Label { FontSize = 13, TextColor = _text };
                host.SetBinding(Label.TextProperty, nameof(ScanRow.Host));
                var kind = new Label { FontSize = 12, TextColor = _muted };
                kind.SetBinding(Label.TextProperty, nameof(ScanRow.KindHint));
                var ports = new Label { FontSize = 12, TextColor = _muted, LineBreakMode = LineBreakMode.TailTruncation };
                ports.SetBinding(Label.TextProperty, nameof(ScanRow.PortsText));
                var btn = new Button
                {
                    FontSize = 12,
                    CornerRadius = 8,
                    Padding = new Thickness(10, 6),
                    BackgroundColor = _blue,
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
            BackgroundColor = _card,
            TextColor = _text,
            BorderColor = _line,
            BorderWidth = 1,
            CornerRadius = 8,
            Padding = new Thickness(14, 8),
            HorizontalOptions = LayoutOptions.End
        };
        closeBtn.Clicked += (_, _) => Close();

        var subnetCol = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = Loc.T("Tool.VideoWall.Scan.Subnet"), FontSize = 12, TextColor = _muted },
                _subnet
            }
        };
        var vendorCol = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = Loc.T("Tool.VideoWall.Scan.Vendor"), FontSize = 12, TextColor = _muted },
                _vendor
            }
        };
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
        Grid.SetRow(topRow, 1);

        var userCol = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = Loc.T("Tool.VideoWall.Scan.Username"), FontSize = 12, TextColor = _muted },
                _user
            }
        };
        var passCol = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = Loc.T("Tool.VideoWall.Scan.Password"), FontSize = 12, TextColor = _muted },
                _pass
            }
        };
        Grid.SetColumn(passCol, 1);
        var credRow = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
            Children = { userCol, passCol }
        };
        Grid.SetRow(credRow, 2);

        _list.BackgroundColor = _listBg;
        var listBorder = new Border
        {
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = _listBg,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = 4,
            Content = _list
        };
        Grid.SetRow(listBorder, 3);
        Grid.SetRow(_status, 4);
        Grid.SetRow(closeBtn, 5);

        var cardContent = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            Padding = 16,
            WidthRequest = 560,
            MaximumWidthRequest = 620,
            MaximumHeightRequest = 520,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(24),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Shadow = new Shadow { Brush = Colors.Black, Offset = new Point(0, 8), Radius = 24, Opacity = 0.22f },
            Content = new Grid
            {
                RowDefinitions =
                [
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                ],
                RowSpacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = Loc.T("Tool.VideoWall.Scan.Title"),
                        FontAttributes = FontAttributes.Bold,
                        FontSize = 16,
                        TextColor = _text
                    },
                    topRow,
                    credRow,
                    listBorder,
                    _status,
                    closeBtn
                }
            }
        };
        cardContent.GestureRecognizers.Add(new TapGestureRecognizer());

        var dim = new BoxView { Color = Color.FromArgb("#80000000") };
        dim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Close) });

        Host = new Grid
        {
            IsVisible = false,
            ClassId = "scanHost",
            ZIndex = 350,
            Children = { dim, cardContent }
        };
    }

    public void Show()
    {
        Host.IsVisible = true;
        _ = StartScanAsync();
    }

    public void Close()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        Host.IsVisible = false;
        Closed?.Invoke();
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
