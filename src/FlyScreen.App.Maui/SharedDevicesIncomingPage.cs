using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.SharedDevices;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

public sealed class SharedDevicesIncomingPage : ContentPage
{
    private readonly TaskCompletionSource<(bool Accept, bool Remember, Dictionary<string, string> Devices)> _result = new();
    private readonly SharedDevicesService _sd;
    private readonly SharedDevicesIncomingRequest _req;
    private readonly CheckBox _remember;
    private readonly Dictionary<string, (Picker Picker, List<SharedLocalDevice> Items)> _picks = new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    public Task<(bool Accept, bool Remember, Dictionary<string, string> Devices)> Result => _result.Task;

    public SharedDevicesIncomingPage(SharedDevicesService sd, SharedDevicesIncomingRequest req)
    {
        _sd = sd;
        _req = req;
        Title = Loc.T("Tool.SharedDevices.IncomingTitle");
        NavigationPage.SetHasNavigationBar(this, false);
        MauiThemeService.Apply(MauiThemeService.Current, this);

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Colors.Gray;
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");
        var winBg = ColorOf("WinBg") ?? Color.FromArgb("#F7F8FA");

        var kinds = string.Join("、", req.Kinds.Select(ToolCardRows.LocalizeKind));
        var body = string.Format(Loc.T("Tool.SharedDevices.IncomingBody"), req.FromPeer, kinds);

        var deviceStack = new VerticalStackLayout { Spacing = 10 };
        foreach (var kind in req.Kinds)
        {
            var picker = new Picker { Title = ToolCardRows.LocalizeKind(kind) };
            var bag = new List<SharedLocalDevice>();
            Fill(picker, bag, kind);
            _picks[kind] = (picker, bag);
            deviceStack.Children.Add(new Border
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
                        new Label
                        {
                            Text = ToolCardRows.LocalizeKind(kind),
                            FontAttributes = FontAttributes.Bold,
                            TextColor = text
                        },
                        picker
                    }
                }
            });
        }

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
        accept.Clicked += async (_, _) =>
        {
            _completed = true;
            _result.TrySetResult((true, _remember.IsChecked, Collect()));
            await Navigation.PopModalAsync();
        };
        var reject = Outline(Loc.T("Tool.SharedDevices.Reject"), text, line);
        reject.Clicked += async (_, _) =>
        {
            _completed = true;
            _result.TrySetResult((false, false, new Dictionary<string, string>()));
            await Navigation.PopModalAsync();
        };

        Content = new ScrollView
        {
            BackgroundColor = winBg,
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    new Label
                    {
                        Text = Loc.T("Tool.SharedDevices.IncomingTitle"),
                        FontSize = 15,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = text
                    },
                    new Label { Text = body, TextColor = text, LineBreakMode = LineBreakMode.WordWrap },
                    deviceStack,
                    rememberRow,
                    new Label
                    {
                        Text = Loc.T("Tool.SharedDevices.RememberHint"),
                        FontSize = 12,
                        TextColor = muted,
                        LineBreakMode = LineBreakMode.WordWrap
                    },
                    new HorizontalStackLayout { Spacing = 8, Children = { accept, reject } }
                }
            }
        };
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_completed)
            _result.TrySetResult((false, false, new Dictionary<string, string>()));
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

    private Dictionary<string, string> Collect()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, (picker, bag)) in _picks)
        {
            if (picker.SelectedIndex < 0 || picker.SelectedIndex >= bag.Count) continue;
            map[kind] = bag[picker.SelectedIndex].Id;
        }

        return map;
    }

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
