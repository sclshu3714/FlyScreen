using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools.SharedDevices;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

public sealed class SharedDevicesManagePage : ContentPage
{
    /// <summary>横屏 / 宽屏内容上限；竖屏不强制宽度。</summary>
    private const double ContentMaxWidth = 440;
    private readonly TaskCompletionSource _closed = new();
    private readonly SharedDevicesService _sd;
    private readonly Picker _speaker;
    private readonly Picker _mic;
    private readonly Picker _camera;
    private readonly List<SharedLocalDevice> _speakerItems = [];
    private readonly List<SharedLocalDevice> _micItems = [];
    private readonly List<SharedLocalDevice> _cameraItems = [];
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
        var controlBg = ColorOf("ControlBg") ?? Colors.White;

        _speaker = MakePicker(text, controlBg);
        _mic = MakePicker(text, controlBg);
        _camera = MakePicker(text, controlBg);
        Fill(_speaker, _speakerItems, SharedDeviceKinds.Speaker);
        Fill(_mic, _micItems, SharedDeviceKinds.Microphone);
        Fill(_camera, _cameraItems, SharedDeviceKinds.Camera);

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
            Apply(_speaker, _speakerItems, SharedDeviceKinds.Speaker);
            Apply(_mic, _micItems, SharedDeviceKinds.Microphone);
            Apply(_camera, _cameraItems, SharedDeviceKinds.Camera);
            _closed.TrySetResult();
            await Navigation.PopModalAsync();
        };
        var cancel = Outline(Loc.T("Tool.SharedDevices.Cancel"), text, line);
        cancel.Clicked += async (_, _) =>
        {
            _closed.TrySetResult();
            await Navigation.PopModalAsync();
        };

        var actions = new HorizontalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.Center,
            Children = { save, cancel }
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
                DeviceCard(card, line, text, Loc.T("Tool.SharedDevices.Speaker"), _speaker),
                DeviceCard(card, line, text, Loc.T("Tool.SharedDevices.Microphone"), _mic),
                DeviceCard(card, line, text, Loc.T("Tool.SharedDevices.Camera"), _camera),
                clearBtn,
                _clearStatus,
                actions
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

    /// <summary>
    /// 竖屏窄屏：铺满可用宽；横屏/宽屏：居中且 ≤ ContentMaxWidth。
    /// </summary>
    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 0 || double.IsNaN(width)) return;

        var landscape = width > height;
        var narrow = DeviceInfo.Idiom == DeviceIdiom.Phone || width < 600;

        var padH = narrow ? (landscape ? 20 : 12) : (landscape ? 28 : 20);
        var padV = narrow ? (landscape ? 10 : 14) : 20;
        _host.Padding = new Thickness(padH, padV);

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

    private void Fill(Picker picker, List<SharedLocalDevice> bag, string kind)
    {
        bag.Clear();
        picker.Items.Clear();
        var list = _sd.EnumerateLocalDevices(kind);
        if (list.Count == 0)
        {
            picker.Items.Add(Loc.T("Tool.SharedDevices.NoLocalDevice"));
            picker.SelectedIndex = 0;
            picker.IsEnabled = false;
            return;
        }

        var selected = _sd.GetSelectedDeviceId(kind);
        var idx = 0;
        for (var i = 0; i < list.Count; i++)
        {
            bag.Add(list[i]);
            picker.Items.Add(list[i].Name);
            if (list[i].Id == selected) idx = i;
        }

        picker.SelectedIndex = idx;
    }

    private void Apply(Picker picker, List<SharedLocalDevice> bag, string kind)
    {
        if (picker.SelectedIndex < 0 || picker.SelectedIndex >= bag.Count) return;
        _sd.SetSelectedDevice(kind, bag[picker.SelectedIndex].Id);
    }

    private static Picker MakePicker(Color text, Color bg) => new()
    {
        Title = Loc.T("Tool.SharedDevices.PickLocalDevice"),
        FontSize = 14,
        TextColor = text,
        BackgroundColor = bg,
        HeightRequest = 44,
        MinimumHeightRequest = 44,
        HorizontalOptions = LayoutOptions.Fill,
        VerticalOptions = LayoutOptions.Center
    };

    private static View DeviceCard(Color card, Color line, Color text, string title, Picker picker)
    {
        var pickerHost = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = picker.BackgroundColor,
            Padding = new Thickness(10, 0),
            HeightRequest = 44,
            MinimumHeightRequest = 44,
            HorizontalOptions = LayoutOptions.Fill,
            Content = picker
        };

        return new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = card,
            Padding = 14,
            HorizontalOptions = LayoutOptions.Fill,
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(0, 1),
                Radius = 6,
                Opacity = 0.06f
            },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label
                    {
                        Text = title,
                        FontSize = 14,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = text
                    },
                    pickerHost
                }
            }
        };
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
