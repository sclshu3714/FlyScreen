using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.SharedDevices;
using FlyScreen.ToolSdk;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

/// <summary>
/// 共享设备种类选择行：自绘样式跟主题，点击用 ActionSheet 选设备（避开各端 Picker 白底/裁字）。
/// </summary>
public sealed class SharedDevicesDeviceSelector
{
    private readonly Page _host;
    private readonly SharedDevicesService _sd;
    private readonly string _kind;
    private readonly Label _valueLabel;
    private readonly List<SharedLocalDevice> _items = [];
    private string? _selectedId;

    public View View { get; }
    public string Kind => _kind;
    public string? SelectedId => _selectedId;

    public SharedDevicesDeviceSelector(
        Page host,
        SharedDevicesService sd,
        string kind,
        string title,
        Color text,
        Color muted,
        Color line,
        Color card,
        Color controlBg)
    {
        _host = host;
        _sd = sd;
        _kind = kind;

        _valueLabel = new Label
        {
            FontSize = 14,
            TextColor = text,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalOptions = LayoutOptions.Fill
        };

        var chevron = new Label
        {
            Text = "▾",
            FontSize = 14,
            TextColor = muted,
            VerticalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            Children = { _valueLabel, chevron }
        };
        Grid.SetColumn(chevron, 1);

        var field = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = controlBg,
            Padding = new Thickness(12, 12),
            MinimumHeightRequest = 48,
            HorizontalOptions = LayoutOptions.Fill,
            Content = row
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await PickAsync();
        field.GestureRecognizers.Add(tap);

        View = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = card,
            Padding = 14,
            HorizontalOptions = LayoutOptions.Fill,
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = title,
                        FontSize = 14,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = text
                    },
                    field
                }
            }
        };

        Reload();
    }

    public void Reload()
    {
        _items.Clear();
        _items.AddRange(_sd.EnumerateLocalDevices(_kind));
        var prefer = _sd.GetSelectedDeviceId(_kind);
        SharedLocalDevice? hit = null;
        if (!string.IsNullOrEmpty(prefer))
            hit = _items.FirstOrDefault(d => d.Id == prefer);
        hit ??= _items.FirstOrDefault(d => d.IsDefault) ?? _items.FirstOrDefault();
        _selectedId = hit?.Id;
        _valueLabel.Text = hit?.Name ?? Loc.T("Tool.SharedDevices.NoLocalDevice");
        View.Opacity = _items.Count == 0 ? 0.55 : 1;
        View.InputTransparent = _items.Count == 0;
    }

    public void ApplyToService()
    {
        if (!string.IsNullOrEmpty(_selectedId))
            _sd.SetSelectedDevice(_kind, _selectedId);
    }

    private async Task PickAsync()
    {
        if (_items.Count == 0) return;
        var names = _items.Select(d => d.Name).ToArray();
        var pick = await _host.DisplayActionSheetAsync(
            Loc.T("Tool.SharedDevices.PickLocalDevice"),
            Loc.T("Tool.Cancel"),
            null,
            names);
        if (string.IsNullOrEmpty(pick) || pick == Loc.T("Tool.Cancel"))
            return;
        var device = _items.FirstOrDefault(d => d.Name == pick);
        if (device == null) return;
        _selectedId = device.Id;
        _valueLabel.Text = device.Name;
    }
}

