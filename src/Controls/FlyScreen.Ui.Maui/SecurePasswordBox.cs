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

    private const string EyeOpenPath =
        "M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z";

    private const string EyeOffPath =
        "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z";

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
            Data = ParseGeometry(EyeOffPath),
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
            WidthRequest = 36,
            HeightRequest = 32,
            Padding = new Thickness(8, 0),
            Content = _eyePath,
            VerticalOptions = LayoutOptions.Center
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => IsRevealed = !IsRevealed;
        _eyeHost.GestureRecognizers.Add(tap);

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
        _eyePath.Data = ParseGeometry(IsRevealed ? EyeOpenPath : EyeOffPath);
        ApplyEyeTip();
    }

    private void ApplyEyeTip()
    {
        var tip = IsRevealed ? HidePasswordTip : ShowPasswordTip;
        ToolTipProperties.SetText(_eyeHost, tip ?? "");
    }

    private static Geometry? ParseGeometry(string data)
        => new PathGeometryConverter().ConvertFromInvariantString(data) as Geometry;

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
