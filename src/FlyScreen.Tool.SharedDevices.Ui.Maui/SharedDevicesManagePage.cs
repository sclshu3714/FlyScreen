using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.SharedDevices;

namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

public sealed class SharedDevicesManagePage : ContentPage
{
    private readonly TaskCompletionSource _closed = new();
    private readonly SharedDevicesService _sd;
    private readonly SharedDevicesDeviceSelector _speaker;
    private readonly SharedDevicesDeviceSelector _mic;
    private readonly SharedDevicesDeviceSelector _camera;
    private readonly Label _clearStatus;
    private readonly Grid _host;
    private readonly Border _shell;

    public Task Closed => _closed.Task;

    public SharedDevicesManagePage(SharedDevicesService sd)
    {
        _sd = sd;
        Title = Loc.T("Tool.SharedDevices.ManageTitle");
        NavigationPage.SetHasNavigationBar(this, false);

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

        foreach (var v in new[] { _speaker.View, _mic.View, _camera.View })
            v.HorizontalOptions = LayoutOptions.Fill;

        var column = new VerticalStackLayout
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

        // 固定宽外壳：测量边界由壳决定，内部选项文字只能截断
        _shell = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding = 0,
            HorizontalOptions = LayoutOptions.Center,
            Content = column
        };

        _host = new Grid
        {
            Padding = new Thickness(12, 16),
            HorizontalOptions = LayoutOptions.Fill,
            Children = { _shell }
        };

        Content = new ScrollView
        {
            BackgroundColor = winBg,
            Orientation = ScrollOrientation.Vertical,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
            // 禁止横向按内容无限测宽
            Content = new Grid
            {
                HorizontalOptions = LayoutOptions.Fill,
                Children = { _host }
            }
        };

        SizeChanged += OnPageSizeChanged;
        SharedDevicesMauiLayout.Apply(_host, _shell, Width, Height);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _speaker.Reload();
        _mic.Reload();
        _camera.Reload();
        SharedDevicesMauiLayout.Apply(_host, _shell, Width, Height);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SizeChanged -= OnPageSizeChanged;
        _closed.TrySetResult();
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
        => SharedDevicesMauiLayout.Apply(_host, _shell, Width, Height);

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
