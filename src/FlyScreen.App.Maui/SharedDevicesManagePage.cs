using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.SharedDevices;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

public sealed class SharedDevicesManagePage : ContentPage
{
    private readonly TaskCompletionSource _closed = new();
    private readonly SharedDevicesService _sd;
    private readonly Picker _speaker;
    private readonly Picker _mic;
    private readonly Picker _camera;
    private readonly List<SharedLocalDevice> _speakerItems = [];
    private readonly List<SharedLocalDevice> _micItems = [];
    private readonly List<SharedLocalDevice> _cameraItems = [];
    private readonly Label _clearStatus;

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

        _speaker = MakePicker();
        _mic = MakePicker();
        _camera = MakePicker();
        Fill(_speaker, _speakerItems, SharedDeviceKinds.Speaker);
        Fill(_mic, _micItems, SharedDeviceKinds.Microphone);
        Fill(_camera, _cameraItems, SharedDeviceKinds.Camera);

        _clearStatus = new Label { FontSize = 12, TextColor = muted };
        var clearBtn = Outline(Loc.T("Tool.SharedDevices.ClearRemember"), text, line);
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

        Content = new ScrollView
        {
            BackgroundColor = winBg,
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = Loc.T("Tool.SharedDevices.ManageTitle"),
                        FontSize = 15,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = text
                    },
                    new Label
                    {
                        Text = Loc.T("Tool.SharedDevices.PickLocalDevice"),
                        FontSize = 12,
                        TextColor = muted
                    },
                    Section(card, line, Loc.T("Tool.SharedDevices.Speaker"), _speaker, text),
                    Section(card, line, Loc.T("Tool.SharedDevices.Microphone"), _mic, text),
                    Section(card, line, Loc.T("Tool.SharedDevices.Camera"), _camera, text),
                    clearBtn,
                    _clearStatus,
                    new HorizontalStackLayout { Spacing = 8, Children = { save, cancel } }
                }
            }
        };
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _closed.TrySetResult();
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

    private static Picker MakePicker() => new() { Title = Loc.T("Tool.SharedDevices.PickLocalDevice") };

    private static View Section(Color card, Color line, string title, View body, Color text) => new Border
    {
        Stroke = line,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 10 },
        BackgroundColor = card,
        Padding = 12,
        Content = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = title, FontAttributes = FontAttributes.Bold, TextColor = text },
                body
            }
        }
    };

    private static Button Primary(string t, Color blue) => new()
    {
        Text = t, BackgroundColor = blue, TextColor = Colors.White, CornerRadius = 8,
        Padding = new Thickness(14, 8)
    };

    private static Button Outline(string t, Color fg, Color line) => new()
    {
        Text = t, BackgroundColor = Colors.Transparent, TextColor = fg, BorderColor = line,
        BorderWidth = 1, CornerRadius = 8, Padding = new Thickness(14, 8)
    };

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;
}
