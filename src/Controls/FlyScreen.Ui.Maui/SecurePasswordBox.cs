using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Ui.Maui;

/// <summary>
/// 独立安全密码框：单一边框输入框，显隐眼睛嵌在右侧（无「外框套内框」）。
/// </summary>
public sealed class SecurePasswordBox : ContentView
{
    private const string FlatStyleClass = "SecurePasswordFlat";

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
    private static int _mapperHooked;

    static SecurePasswordBox()
        => EnsureFlatEntryMapper();

    public SecurePasswordBox()
    {
        BackgroundColor = Colors.Transparent;

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
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0),
            StyleClass = [FlatStyleClass]
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
        _entry.HandlerChanged += (_, _) => FlattenNativeEntry(_entry);

        _eyePath = new ShapePath
        {
            Data = ParseGeometry(IconCatalog.GetPath(IconCodes.EyeOff)),
            Aspect = Stretch.Uniform,
            Fill = muted,
            WidthRequest = 16,
            HeightRequest = 16,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
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

        // 单层边框：Entry 去原生描边后与眼睛同处 chrome 内
        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(32))
            ],
            ColumnSpacing = 0,
            Padding = new Thickness(10, 0, 4, 0),
            VerticalOptions = LayoutOptions.Fill
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
            HeightRequest = 36,
            Padding = new Thickness(0),
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            Content = row
        };
        Content = _chrome;
        HeightRequest = 36;
        EnsureFlatEntryMapper();
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
        FlattenNativeEntry(_entry);
    }

    private static void EnsureFlatEntryMapper()
    {
        if (Interlocked.Exchange(ref _mapperHooked, 1) != 0) return;
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping(FlatStyleClass, (handler, view) =>
        {
            if (view is not Entry entry) return;
            if (entry.StyleClass is null || !entry.StyleClass.Contains(FlatStyleClass)) return;
            FlattenNativeEntry(entry);
        });
    }

    private static void FlattenNativeEntry(Entry entry)
    {
        var native = entry.Handler?.PlatformView;
        if (native is null) return;

#if MAUI_WINDOWS
        if (native is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            tb.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            tb.Style = null;
            tb.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            tb.Padding = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 4);
            try
            {
                tb.Resources["TextControlBorderThemeThickness"] = new Microsoft.UI.Xaml.Thickness(0);
                tb.Resources["TextControlBorderThemeThicknessFocused"] = new Microsoft.UI.Xaml.Thickness(0);
                tb.Resources["TextControlBorderThemeThicknessPointerOver"] = new Microsoft.UI.Xaml.Thickness(0);
                tb.Resources["TextControlBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                tb.Resources["TextControlBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                tb.Resources["TextControlBackgroundFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            catch
            {
                /* ignore theme key differences */
            }
            return;
        }
#endif

        // Android EditText：纯反射清默认下划线/背景（Ui.Maui 无 android TFM）
        if (OperatingSystem.IsAndroid())
        {
            try
            {
                var t = native.GetType();
                foreach (var name in new[] { "SetBackground", "SetBackgroundDrawable" })
                {
                    foreach (var m in t.GetMethods().Where(m => m.Name == name && m.GetParameters().Length == 1))
                    {
                        try { m.Invoke(native, [null]); break; }
                        catch { /* try next overload */ }
                    }
                }

                t.GetMethod("SetPadding", [typeof(int), typeof(int), typeof(int), typeof(int)])
                    ?.Invoke(native, [0, 0, 0, 0]);
                t.GetProperty("Background")?.SetValue(native, null);
            }
            catch
            {
                /* ignore */
            }
        }
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
