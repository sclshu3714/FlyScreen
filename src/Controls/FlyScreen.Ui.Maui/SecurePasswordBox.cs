using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Ui.Maui;

/// <summary>
/// 独立安全密码框：单边框内左侧输入、右侧眼睛显隐。文案/主题由使用方或宿主资源提供。
/// </summary>
public sealed class SecurePasswordBox : ContentView
{
    public static readonly BindableProperty PasswordProperty = BindableProperty.Create(
        nameof(Password), typeof(string), typeof(SecurePasswordBox), string.Empty,
        BindingMode.TwoWay, propertyChanged: OnPasswordChanged);

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder), typeof(string), typeof(SecurePasswordBox), string.Empty,
        propertyChanged: OnPlaceholderChanged);

    public static readonly BindableProperty IsRevealedProperty = BindableProperty.Create(
        nameof(IsRevealed), typeof(bool), typeof(SecurePasswordBox), false,
        propertyChanged: OnIsRevealedChanged);

    public static readonly BindableProperty ShowPasswordTipProperty = BindableProperty.Create(
        nameof(ShowPasswordTip), typeof(string), typeof(SecurePasswordBox), string.Empty,
        propertyChanged: OnTipChanged);

    public static readonly BindableProperty HidePasswordTipProperty = BindableProperty.Create(
        nameof(HidePasswordTip), typeof(string), typeof(SecurePasswordBox), string.Empty,
        propertyChanged: OnTipChanged);

    private readonly Border _chrome;
    private readonly Entry _entry;
    private readonly ShapePath _eyePath;
    private readonly Border _eyeHost;
    private bool _syncing;

    public SecurePasswordBox()
    {
        var text = ResolveColor("Text", Color.FromArgb("#111827"));
        var muted = ResolveColor("Muted", Color.FromArgb("#6B7280"));
        var line = ResolveColor("ControlBorder", Color.FromArgb("#D1D5DB"));
        var bg = ResolveColor("ControlBg", Colors.White);

        _entry = new Entry
        {
            IsPassword = true,
            TextColor = text,
            PlaceholderColor = muted,
            BackgroundColor = Colors.Transparent,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0)
        };
        _entry.TextChanged += (_, e) =>
        {
            if (_syncing) return;
            _syncing = true;
            Password = e.NewTextValue ?? "";
            _syncing = false;
            PasswordChanged?.Invoke(this, EventArgs.Empty);
        };
        _entry.Focused += (_, _) => ApplyFocusChrome(true);
        _entry.Unfocused += (_, _) => ApplyFocusChrome(false);

        _eyePath = new ShapePath
        {
            Data = ParseGeometry(IconCatalog.GetPath(IconCodes.EyeOff)),
            Aspect = Stretch.Uniform,
            Fill = muted,
            WidthRequest = 18,
            HeightRequest = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        _eyeHost = new Border
        {
            BackgroundColor = Colors.Transparent,
            StrokeThickness = 0,
            Stroke = Colors.Transparent,
            WidthRequest = 28,
            HeightRequest = 28,
            Padding = new Thickness(0),
            Content = _eyePath,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => IsRevealed = !IsRevealed;
        _eyeHost.GestureRecognizers.Add(tap);
        ApplyEyeHoverVisualStates(_eyeHost);

        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 0,
            Padding = new Thickness(10, 0, 2, 0)
        };
        row.Children.Add(_entry);
        Grid.SetColumn(_eyeHost, 1);
        row.Children.Add(_eyeHost);

        _chrome = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = bg,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            MinimumHeightRequest = 36,
            Padding = new Thickness(0),
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            Content = row
        };
        Content = _chrome;
        HeightRequest = 36;
    }

    public event EventHandler? PasswordChanged;

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value ?? "");
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value ?? "");
    }

    public bool IsRevealed
    {
        get => (bool)GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    public string ShowPasswordTip
    {
        get => (string)GetValue(ShowPasswordTipProperty);
        set => SetValue(ShowPasswordTipProperty, value ?? "");
    }

    public string HidePasswordTip
    {
        get => (string)GetValue(HidePasswordTipProperty);
        set => SetValue(HidePasswordTipProperty, value ?? "");
    }

    public Entry InnerEntry => _entry;

    public void ApplyTheme(Color text, Color muted, Color line, Color card)
    {
        _entry.TextColor = text;
        _entry.PlaceholderColor = muted;
        _eyePath.Fill = muted;
        _chrome.Stroke = line;
        _chrome.BackgroundColor = card;
    }

    private void ApplyRevealState()
    {
        _entry.IsPassword = !IsRevealed;
        _eyePath.Data = ParseGeometry(IconCatalog.GetPath(IsRevealed ? IconCodes.EyeOpen : IconCodes.EyeOff));
        ApplyEyeTip();
    }

    private void ApplyEyeTip()
    {
        var tip = IsRevealed ? HidePasswordTip : ShowPasswordTip;
        ToolTipProperties.SetText(_eyeHost, tip ?? "");
    }

    private static Geometry? ParseGeometry(string data)
        => new PathGeometryConverter().ConvertFromInvariantString(data) as Geometry;

    /// <summary>桌面 PointerOver 轻量悬浮；触控端无 PointerOver 则保持透明。</summary>
    private static void ApplyEyeHoverVisualStates(Border eyeHost)
    {
        var groups = VisualStateManager.GetVisualStateGroups(eyeHost);
        var common = new VisualStateGroup { Name = "CommonStates" };
        common.States.Add(new VisualState { Name = "Normal" });
        var pointerOver = new VisualState { Name = "PointerOver" };
        pointerOver.Setters.Add(new Setter
        {
            Property = Border.BackgroundColorProperty,
            Value = Color.FromRgba(0, 0, 0, 0.09)
        });
        common.States.Add(pointerOver);
        groups.Add(common);
    }

    private void ApplyFocusChrome(bool focused)
    {
        var blue = ResolveColor("Blue", Color.FromArgb("#2563EB"));
        var line = ResolveColor("ControlBorder", Color.FromArgb("#D1D5DB"));
        _chrome.Stroke = focused ? blue : line;
    }

    private static Color ResolveColor(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true)
        {
            if (value is Color c) return c;
            if (value is SolidColorBrush b) return b.Color;
        }

        return fallback;
    }

    private static void OnPasswordChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var box = (SecurePasswordBox)bindable;
        if (box._syncing) return;
        var text = newValue as string ?? "";
        if (box._entry.Text == text) return;
        box._syncing = true;
        box._entry.Text = text;
        box._syncing = false;
    }

    private static void OnPlaceholderChanged(BindableObject bindable, object oldValue, object newValue)
        => ((SecurePasswordBox)bindable)._entry.Placeholder = newValue as string ?? "";

    private static void OnIsRevealedChanged(BindableObject bindable, object oldValue, object newValue)
        => ((SecurePasswordBox)bindable).ApplyRevealState();

    private static void OnTipChanged(BindableObject bindable, object oldValue, object newValue)
        => ((SecurePasswordBox)bindable).ApplyEyeTip();
}
