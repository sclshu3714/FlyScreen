using System.Globalization;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools.SharedDevices;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

/// <summary>对齐 Win SharedDevicesStartDialog：选布局内电脑 + 设备种类后确认申请。</summary>
public sealed class SharedDevicesStartPage : ContentPage
{
    private sealed class PeerRow
    {
        public required SharedDevicesTargetInfo Info { get; init; }
        public string Display { get; init; } = "";
        public bool Selectable { get; init; }
    }

    private readonly TaskCompletionSource<(string? Peer, string[] Kinds)> _tcs = new();
    private readonly CollectionView _peerList = new()
    {
        SelectionMode = SelectionMode.Single,
        HeightRequest = 180
    };
    private readonly CheckBox _kindSpeaker = new() { IsChecked = true };
    private readonly CheckBox _kindMicrophone = new() { IsChecked = true };
    private readonly CheckBox _kindCamera = new() { IsChecked = true };
    private readonly Label _hint = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private PeerRow? _selected;

    public Task<(string? Peer, string[] Kinds)> Result => _tcs.Task;

    public SharedDevicesStartPage(IReadOnlyList<SharedDevicesTargetInfo> targets)
    {
        Title = Loc.T("Tool.SharedDevices.StartTitle");
        NavigationPage.SetHasNavigationBar(this, false);
        MauiThemeService.Apply(MauiThemeService.Current, this);

        var rows = new List<PeerRow>();
        foreach (var t in targets)
        {
            rows.Add(new PeerRow
            {
                Info = t,
                Display = t.FormatDisplayName(),
                Selectable = t.CanProvide
            });
        }

        _peerList.ItemsSource = rows;
        _peerList.ItemTemplate = new DataTemplate(() =>
        {
            var label = new Label
            {
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(10, 8)
            };
            label.SetBinding(Label.TextProperty, nameof(PeerRow.Display));
            label.SetBinding(Label.TextColorProperty,
                new Binding(nameof(PeerRow.Selectable),
                    converter: new SelectableTextColorConverter()));
            return new Border
            {
                StrokeThickness = 0,
                Padding = 0,
                Content = label
            };
        });
        _peerList.SelectionChanged += (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is PeerRow row && !row.Selectable)
            {
                _peerList.SelectedItem = null;
                _selected = null;
                return;
            }

            _selected = e.CurrentSelection.FirstOrDefault() as PeerRow;
        };

        if (rows.Count == 0)
            _hint.Text = Loc.T("Tool.SharedDevices.NoTargets");
        else
        {
            var first = rows.FirstOrDefault(r => r.Selectable);
            if (first != null)
            {
                _peerList.SelectedItem = first;
                _selected = first;
            }
        }

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Colors.Gray;
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");
        _hint.TextColor = muted;

        var cancelBtn = new Button
        {
            Text = Loc.T("Tool.SharedDevices.Cancel"),
            BackgroundColor = Colors.Transparent,
            TextColor = text,
            BorderColor = line,
            BorderWidth = 1,
            CornerRadius = 8,
            Padding = new Thickness(16, 8),
            MinimumWidthRequest = 88
        };
        cancelBtn.Clicked += async (_, _) =>
        {
            _tcs.TrySetResult((null, []));
            await Navigation.PopModalAsync();
        };

        var okBtn = new Button
        {
            Text = Loc.T("Tool.SharedDevices.Confirm"),
            BackgroundColor = blue,
            TextColor = Colors.White,
            CornerRadius = 8,
            Padding = new Thickness(16, 8),
            MinimumWidthRequest = 88
        };
        okBtn.Clicked += Ok_Clicked;

        Content = new Grid
        {
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.35),
            Padding = 24,
            Children =
            {
                new Border
                {
                    Stroke = line,
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    BackgroundColor = card,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    WidthRequest = 420,
                    MaximumWidthRequest = 480,
                    Padding = 16,
                    Content = new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            new Label
                            {
                                Text = Loc.T("Tool.SharedDevices.StartTitle"),
                                FontSize = 15,
                                FontAttributes = FontAttributes.Bold,
                                TextColor = text
                            },
                            new Label
                            {
                                Text = Loc.T("Tool.SharedDevices.PickPeer"),
                                FontSize = 12,
                                TextColor = muted
                            },
                            new Border
                            {
                                Stroke = line,
                                StrokeThickness = 1,
                                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                                BackgroundColor = ColorOf("ListBg") ?? Color.FromArgb("#F9FAFB"),
                                Padding = 4,
                                Content = _peerList
                            },
                            new Label
                            {
                                Text = Loc.T("Tool.SharedDevices.PickKinds"),
                                FontSize = 12,
                                TextColor = muted,
                                Margin = new Thickness(0, 4, 0, 0)
                            },
                            KindRow(_kindSpeaker, Loc.T("Tool.SharedDevices.Speaker"), text),
                            KindRow(_kindMicrophone, Loc.T("Tool.SharedDevices.Microphone"), text),
                            KindRow(_kindCamera, Loc.T("Tool.SharedDevices.Camera"), text),
                            _hint,
                            new HorizontalStackLayout
                            {
                                Spacing = 8,
                                HorizontalOptions = LayoutOptions.End,
                                Margin = new Thickness(0, 8, 0, 0),
                                Children = { cancelBtn, okBtn }
                            }
                        }
                    }
                }
            }
        };
    }

    private static View KindRow(CheckBox box, string title, Color text)
    {
        var label = new Label
        {
            Text = title,
            FontSize = 13,
            TextColor = text,
            VerticalOptions = LayoutOptions.Center
        };
        var row = new HorizontalStackLayout { Spacing = 8, Children = { box, label } };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => box.IsChecked = !box.IsChecked;
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private async void Ok_Clicked(object? sender, EventArgs e)
    {
        if (_selected is not { Selectable: true } row)
        {
            await DisplayAlertAsync(Title, Loc.T("Tool.SharedDevices.NoTargets"), "OK");
            return;
        }

        var kinds = new List<string>();
        if (_kindSpeaker.IsChecked) kinds.Add(SharedDeviceKinds.Speaker);
        if (_kindMicrophone.IsChecked) kinds.Add(SharedDeviceKinds.Microphone);
        if (_kindCamera.IsChecked) kinds.Add(SharedDeviceKinds.Camera);
        if (kinds.Count == 0)
        {
            await DisplayAlertAsync(Title, Loc.T("Tool.SharedDevices.PickKinds"), "OK");
            return;
        }

        _tcs.TrySetResult((row.Info.PeerName, kinds.ToArray()));
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult((null, []));
    }

    private static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }

    private sealed class SelectableTextColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var selectable = value is true;
            if (Application.Current?.Resources.TryGetValue(selectable ? "Text" : "Muted", out var v) == true
                && v is Color c)
                return c;
            return selectable ? Colors.Black : Colors.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
