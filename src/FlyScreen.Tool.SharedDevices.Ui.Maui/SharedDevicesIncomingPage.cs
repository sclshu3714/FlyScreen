using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;

namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

public sealed class SharedDevicesIncomingPage : ContentPage
{
    private const double ContentMaxWidth = 440;
    private readonly TaskCompletionSource<(bool Accept, bool Remember, Dictionary<string, string> Devices)> _result = new();
    private readonly SharedDevicesService _sd;
    private readonly CheckBox _remember;
    private readonly List<SharedDevicesDeviceSelector> _selectors = [];
    private readonly Grid _host;
    private readonly VerticalStackLayout _column;
    private bool _completed;

    public Task<(bool Accept, bool Remember, Dictionary<string, string> Devices)> Result => _result.Task;

    public SharedDevicesIncomingPage(SharedDevicesService sd, SharedDevicesIncomingRequest req)
    {
        _sd = sd;
        Title = Loc.T("Tool.SharedDevices.IncomingTitle");
        NavigationPage.SetHasNavigationBar(this, false);
        

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Colors.Gray;
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");
        var winBg = ColorOf("WinBg") ?? Color.FromArgb("#F7F8FA");
        var controlBg = ColorOf("ControlBg") ?? ColorOf("ListBg") ?? card;

        var kinds = string.Join("、", req.Kinds.Select(SharedDevicesKindLabels.Localize));
        var body = string.Format(Loc.T("Tool.SharedDevices.IncomingBody"), req.FromPeer, kinds);

        var deviceStack = new VerticalStackLayout { Spacing = 10 };
        foreach (var kind in req.Kinds)
        {
            if (sd.EnumerateLocalDevices(kind).Count == 0)
                continue;

            var sel = new SharedDevicesDeviceSelector(
                this, sd, kind, SharedDevicesKindLabels.Localize(kind),
                text, muted, line, card, controlBg);
            _selectors.Add(sel);
            deviceStack.Children.Add(sel.View);
        }

        if (_selectors.Count == 0)
            body = string.Format(Loc.T("Tool.SharedDevices.IncomingBody"), req.FromPeer,
                Loc.T("Tool.SharedDevices.PeerNoDevice"));

        _remember = new CheckBox { IsChecked = false, Color = blue };
        var rememberRow = new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                _remember,
                new Label
                {
                    Text = Loc.T("Tool.SharedDevices.Remember"),
                    TextColor = text,
                    VerticalOptions = LayoutOptions.Center
                }
            }
        };

        var accept = Primary(Loc.T("Tool.SharedDevices.Accept"), blue);
        accept.IsEnabled = _selectors.Count > 0;
        accept.Clicked += async (_, _) =>
        {
            if (_selectors.Count == 0)
            {
                _completed = true;
                _result.TrySetResult((false, false, new Dictionary<string, string>()));
                await Navigation.PopModalAsync();
                return;
            }

            var devices = Collect();
            if (devices.Count == 0)
            {
                await DisplayAlertAsync(Title, Loc.T("Tool.SharedDevices.NoLocalDevice"), "OK");
                return;
            }

            _completed = true;
            _result.TrySetResult((true, _remember.IsChecked, devices));
            await Navigation.PopModalAsync();
        };
        var reject = Outline(Loc.T("Tool.SharedDevices.Reject"), text, line);
        reject.Clicked += async (_, _) =>
        {
            _completed = true;
            _result.TrySetResult((false, false, new Dictionary<string, string>()));
            await Navigation.PopModalAsync();
        };

        _column = new VerticalStackLayout
        {
            Spacing = 12,
            HorizontalOptions = LayoutOptions.Fill,
            Children =
            {
                new Label
                {
                    Text = Loc.T("Tool.SharedDevices.IncomingTitle"),
                    FontSize = 18,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = text,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                new Label
                {
                    Text = body,
                    TextColor = text,
                    LineBreakMode = LineBreakMode.WordWrap,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                deviceStack,
                rememberRow,
                new Label
                {
                    Text = Loc.T("Tool.SharedDevices.RememberHint"),
                    FontSize = 12,
                    TextColor = muted,
                    LineBreakMode = LineBreakMode.WordWrap
                },
                new HorizontalStackLayout
                {
                    Spacing = 10,
                    HorizontalOptions = LayoutOptions.Center,
                    Children = { accept, reject }
                }
            }
        };

        _host = new Grid
        {
            Padding = new Thickness(12, 16),
            Children = { _column }
        };

        Content = new ScrollView
        {
            BackgroundColor = winBg,
            Orientation = ScrollOrientation.Vertical,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = _host
        };

        SizeChanged += OnPageSizeChanged;
        ApplyResponsiveLayout(Width, Height);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SizeChanged -= OnPageSizeChanged;
        if (!_completed)
            _result.TrySetResult((false, false, new Dictionary<string, string>()));
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
        => ApplyResponsiveLayout(Width, Height);

    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 0 || double.IsNaN(width)) return;
        var landscape = width > height;
        var narrow = DeviceInfo.Idiom == DeviceIdiom.Phone || width < 600;
        _host.Padding = new Thickness(
            narrow ? (landscape ? 20 : 12) : 20,
            narrow ? (landscape ? 10 : 14) : 20);

        if (narrow && !landscape)
        {
            _column.MaximumWidthRequest = double.PositiveInfinity;
            _column.HorizontalOptions = LayoutOptions.Fill;
        }
        else
        {
            _column.MaximumWidthRequest = ContentMaxWidth;
            _column.HorizontalOptions = LayoutOptions.Center;
        }
    }

    private Dictionary<string, string> Collect()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sel in _selectors)
        {
            if (string.IsNullOrEmpty(sel.SelectedId)) continue;
            map[sel.Kind] = sel.SelectedId!;
        }

        return map;
    }

    private static Button Primary(string t, Color blue) => new()
    {
        Text = t,
        BackgroundColor = blue,
        TextColor = Colors.White,
        CornerRadius = 8,
        Padding = new Thickness(18, 10),
        MinimumHeightRequest = 40,
        MinimumWidthRequest = 88
    };

    private static Button Outline(string t, Color fg, Color line) => new()
    {
        Text = t,
        BackgroundColor = Colors.Transparent,
        TextColor = fg,
        BorderColor = line,
        BorderWidth = 1,
        CornerRadius = 8,
        Padding = new Thickness(18, 10),
        MinimumHeightRequest = 40,
        MinimumWidthRequest = 88
    };

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;
}

