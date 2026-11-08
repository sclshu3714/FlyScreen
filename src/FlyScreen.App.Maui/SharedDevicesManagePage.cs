using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools.SharedDevices;

namespace FlyScreen.AppMaui;

public sealed class SharedDevicesManagePage : ContentPage
{
    private const double ContentMaxWidth = 440;
    private readonly TaskCompletionSource _closed = new();
    private readonly SharedDevicesService _sd;
    private readonly SharedDevicesDeviceSelector _speaker;
    private readonly SharedDevicesDeviceSelector _mic;
    private readonly SharedDevicesDeviceSelector _camera;
    private readonly Label _clearStatus;
    private readonly Grid _host;
    private readonly VerticalStackLayout _column;

    public Task Closed => _closed.Task;

    public SharedDevicesManagePage(SharedDevicesService sd)
    {
        _sd = sd;
        Title = Loc.T("Tool.SharedDevices.ManageTitle");
        NavigationPage.SetHasNavigationBar(this, false);
        MauiThemeService.Apply(MauiThemeService.Current, this);

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Colors.Gray;
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");
        var winBg = ColorOf("WinBg") ?? Color.FromArgb("#F7F8FA");
        var controlBg = ColorOf("ControlBg") ?? ColorOf("ListBg") ?? card;

        _speaker = new SharedDevicesDeviceSelector(
            this, sd, SharedDeviceKinds.Speaker, Loc.T("Tool.SharedDevices.Speaker"),
            text, muted, line, card, controlBg);
        _mic = new SharedDevicesDeviceSelector(
            this, sd, SharedDeviceKinds.Microphone, Loc.T("Tool.SharedDevices.Microphone"),
            text, muted, line, card, controlBg);
        _camera = new SharedDevicesDeviceSelector(
            this, sd, SharedDeviceKinds.Camera, Loc.T("Tool.SharedDevices.Camera"),
            text, muted, line, card, controlBg);

        _clearStatus = new Label
        {
            FontSize = 12,
            TextColor = muted,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.WordWrap
        };
        var clearBtn = Outline(Loc.T("Tool.SharedDevices.ClearRemember"), text, line);
        clearBtn.HorizontalOptions = LayoutOptions.Fill;
        clearBtn.Clicked += (_, _) =>
        {
            _sd.ClearRemember();
            _clearStatus.Text = Loc.T("Tool.SharedDevices.ClearRememberDone");
        };

        var save = Primary(Loc.T("Tool.SharedDevices.Save"), blue);
        save.Clicked += async (_, _) =>
        {
            _speaker.ApplyToService();
            _mic.ApplyToService();
            _camera.ApplyToService();
            _closed.TrySetResult();
            await Navigation.PopModalAsync();
        };
        var cancel = Outline(Loc.T("Tool.SharedDevices.Cancel"), text, line);
        cancel.Clicked += async (_, _) =>
        {
            _closed.TrySetResult();
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
                    Text = Loc.T("Tool.SharedDevices.ManageTitle"),
                    FontSize = 18,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = text,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                new Label
                {
                    Text = Loc.T("Tool.SharedDevices.PickLocalDevice"),
                    FontSize = 12,
                    TextColor = muted,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                },
                _speaker.View,
                _mic.View,
                _camera.View,
                clearBtn,
                _clearStatus,
                new HorizontalStackLayout
                {
                    Spacing = 10,
                    HorizontalOptions = LayoutOptions.Center,
                    Children = { save, cancel }
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
        _closed.TrySetResult();
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
