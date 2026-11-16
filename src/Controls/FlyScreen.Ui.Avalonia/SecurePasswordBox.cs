using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FlyScreen.Ui.Avalonia;

/// <summary>
/// 独立安全密码框：单边框内左侧输入、右侧眼睛显隐。文案/主题由使用方或宿主资源提供。
/// </summary>
public sealed class SecurePasswordBox : Border
{
    public static readonly StyledProperty<string> PasswordProperty =
        AvaloniaProperty.Register<SecurePasswordBox, string>(nameof(Password), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<SecurePasswordBox, string>(nameof(Placeholder), "");

    public static readonly StyledProperty<bool> IsRevealedProperty =
        AvaloniaProperty.Register<SecurePasswordBox, bool>(nameof(IsRevealed), false);

    public static readonly StyledProperty<string> ShowPasswordTipProperty =
        AvaloniaProperty.Register<SecurePasswordBox, string>(nameof(ShowPasswordTip), "");

    public static readonly StyledProperty<string> HidePasswordTipProperty =
        AvaloniaProperty.Register<SecurePasswordBox, string>(nameof(HidePasswordTip), "");

    private static readonly Geometry EyeOpenGeometry = Geometry.Parse(
        "M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z");

    private static readonly Geometry EyeOffGeometry = Geometry.Parse(
        "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z");

    private readonly TextBox _box;
    private readonly ShapePath _eyePath;
    private readonly Button _eyeBtn;
    private bool _syncing;

    public SecurePasswordBox()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        MinHeight = 36;
        Height = 36;
        Padding = new Thickness(10, 0, 4, 0);
        Background = ResolveBrush("ControlBg", Brushes.White);
        BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.Parse("#D1D5DB")));

        _box = new TextBox
        {
            PasswordChar = '•',
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 28,
            Foreground = ResolveBrush("Text", Brushes.Black)
        };
        _box.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Password = _box.Text ?? "";
            _syncing = false;
            PasswordChanged?.Invoke(this, EventArgs.Empty);
        };
        _box.GotFocus += (_, _) => ApplyFocusChrome(true);
        _box.LostFocus += (_, _) => ApplyFocusChrome(false);

        _eyePath = new ShapePath
        {
            Data = EyeOffGeometry,
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            Fill = ResolveBrush("Muted", new SolidColorBrush(Color.Parse("#6B7280")))
        };
        _eyeBtn = new Button
        {
            Content = _eyePath,
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _eyeBtn.Click += (_, _) => IsRevealed = !IsRevealed;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        grid.Children.Add(_box);
        Grid.SetColumn(_eyeBtn, 1);
        grid.Children.Add(_eyeBtn);
        Child = grid;

        PointerEntered += (_, _) =>
        {
            if (!_box.IsFocused)
                BorderBrush = ResolveBrush("Blue", new SolidColorBrush(Color.Parse("#2563EB")));
        };
        PointerExited += (_, _) =>
        {
            if (!_box.IsFocused)
                BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.Parse("#D1D5DB")));
        };
    }

    public event EventHandler? PasswordChanged;

    public string Password
    {
        get => GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value ?? "");
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value ?? "");
    }

    public bool IsRevealed
    {
        get => GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    public string ShowPasswordTip
    {
        get => GetValue(ShowPasswordTipProperty);
        set => SetValue(ShowPasswordTipProperty, value ?? "");
    }

    public string HidePasswordTip
    {
        get => GetValue(HidePasswordTipProperty);
        set => SetValue(HidePasswordTipProperty, value ?? "");
    }

    public string InputFontFamily
    {
        get => _box.FontFamily?.Name ?? "";
        set => _box.FontFamily = string.IsNullOrWhiteSpace(value) ? FontFamily.Default : new FontFamily(value);
    }

    public double InputFontSize
    {
        get => _box.FontSize;
        set => _box.FontSize = value;
    }

    public TextBox InnerBox => _box;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PasswordProperty && !_syncing)
        {
            var value = change.GetNewValue<string>() ?? "";
            if (_box.Text != value)
            {
                _syncing = true;
                _box.Text = value;
                _syncing = false;
            }
        }
        else if (change.Property == PlaceholderProperty)
        {
            _box.Watermark = change.GetNewValue<string>() ?? "";
        }
        else if (change.Property == IsRevealedProperty)
        {
            ApplyRevealState();
        }
        else if (change.Property == ShowPasswordTipProperty || change.Property == HidePasswordTipProperty)
        {
            ApplyEyeTip();
        }
    }

    private void ApplyRevealState()
    {
        _box.PasswordChar = IsRevealed ? default : '•';
        _eyePath.Data = IsRevealed ? EyeOpenGeometry : EyeOffGeometry;
        ApplyEyeTip();
    }

    private void ApplyEyeTip()
    {
        var tip = IsRevealed ? HidePasswordTip : ShowPasswordTip;
        ToolTip.SetTip(_eyeBtn, string.IsNullOrEmpty(tip) ? null : tip);
    }

    private void ApplyFocusChrome(bool focused)
    {
        BorderBrush = focused
            ? ResolveBrush("Blue", new SolidColorBrush(Color.Parse("#2563EB")))
            : ResolveBrush("ControlBorder", new SolidColorBrush(Color.Parse("#D1D5DB")));
    }

    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        if (this.TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        return fallback;
    }
}
