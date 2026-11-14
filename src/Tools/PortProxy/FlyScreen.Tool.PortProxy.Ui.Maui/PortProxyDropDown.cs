namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>
/// 下拉触发条；选项经 <see cref="PortProxyFlyoutHost"/> 浮于上层，不撑开布局。
/// </summary>
internal sealed class PortProxyDropDown : ContentView
{
    private readonly Border _trigger;
    private readonly Label _valueLabel;
    private readonly Color _text;
    private readonly Color _line;
    private readonly Color _bg;
    private readonly Color _hover;
    private PortProxyFlyoutHost? _host;
    private string[] _options = [];
    private bool _open;
    private bool _enabled = true;

    public event Action<string>? SelectionChanged;

    public PortProxyDropDown(Color text, Color muted, Color border, Color background, Color? hover = null)
    {
        _text = text;
        _line = border;
        _bg = background;
        _hover = hover ?? Color.FromArgb("#EFF6FF");

        _valueLabel = new Label
        {
            FontSize = 13,
            TextColor = text,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalOptions = LayoutOptions.Fill
        };
        var arrow = new Label
        {
            Text = "▾",
            FontSize = 11,
            TextColor = muted,
            VerticalOptions = LayoutOptions.Center
        };
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            HeightRequest = 34
        };
        row.Add(_valueLabel, 0, 0);
        row.Add(arrow, 1, 0);

        _trigger = new Border
        {
            Stroke = border,
            StrokeThickness = 1,
            BackgroundColor = background,
            Padding = new Thickness(12, 0),
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.Fill,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = row
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (_enabled)
                SetOpen(!_open);
        };
        _trigger.GestureRecognizers.Add(tap);
        Content = _trigger;
    }

    public string Value
    {
        get => _valueLabel.Text ?? "";
        set => _valueLabel.Text = value;
    }

    public void BindFlyout(PortProxyFlyoutHost host) => _host = host;

    public void SetOptions(params string[] options)
    {
        _options = options ?? [];
        if (_open)
            SetOpen(true);
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Opacity = enabled ? 1 : 0.55;
        if (!enabled)
            Close();
    }

    public void Close() => SetOpen(false);

    internal void NotifyFlyoutClosed() => _open = false;

    private void SetOpen(bool open)
    {
        if (!open)
        {
            _open = false;
            _host?.Close(this);
            return;
        }

        if (!_enabled || _options.Length == 0 || _host == null)
            return;

        _open = true;
        _host.Show(this, _trigger, BuildMenu(), Math.Max(WidthRequest > 0 ? WidthRequest : Width, 128));
    }

    private Border BuildMenu()
    {
        var items = new VerticalStackLayout { Spacing = 0 };
        foreach (var opt in _options)
        {
            var label = new Label
            {
                Text = opt,
                FontSize = 13,
                TextColor = _text,
                Padding = new Thickness(12, 10),
                BackgroundColor = Colors.Transparent
            };
            var item = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Colors.Transparent,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Content = label
            };
            var local = opt;
            var itemTap = new TapGestureRecognizer();
            itemTap.Tapped += (_, _) =>
            {
                Value = local;
                Close();
                SelectionChanged?.Invoke(local);
            };
            item.GestureRecognizers.Add(itemTap);

            var hover = new PointerGestureRecognizer();
            hover.PointerEntered += (_, _) => item.BackgroundColor = _hover;
            hover.PointerExited += (_, _) => item.BackgroundColor = Colors.Transparent;
            item.GestureRecognizers.Add(hover);

            items.Children.Add(item);
        }

        return new Border
        {
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = _bg,
            Padding = new Thickness(4),
            MinimumWidthRequest = 128,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(0, 4),
                Radius = 12,
                Opacity = 0.18f
            },
            Content = items,
            ZIndex = 210
        };
    }
}
