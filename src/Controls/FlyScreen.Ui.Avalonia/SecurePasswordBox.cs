using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Ui.Theme.Icons;
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

    private static readonly Geometry EyeOpenGeometry = Geometry.Parse(IconCatalog.GetPath(IconCodes.EyeOpen));
    private static readonly Geometry EyeOffGeometry = Geometry.Parse(IconCatalog.GetPath(IconCodes.EyeOff));

    private static readonly IBrush EyeHoverBrush = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0));

    private readonly TextBox _box;
    private readonly ShapePath _eyePath;
    private readonly Border _eyeHost;
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
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ResolveBrush("Muted", new SolidColorBrush(Color.Parse("#6B7280")))
        };
        // 不用 Button：避免宿主全局 Button MinWidth/主题铬吞样式
        _eyeHost = new Border
        {
            Child = _eyePath,
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _eyeHost.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_eyeHost).Properties.IsLeftButtonPressed)
            {
                IsRevealed = !IsRevealed;
                e.Handled = true;
            }
        };
        _eyeHost.PointerEntered += (_, _) => _eyeHost.Background = EyeHoverBrush;
        _eyeHost.PointerExited += (_, _) => _eyeHost.Background = Brushes.Transparent;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        grid.Children.Add(_box);
        Grid.SetColumn(_eyeHost, 1);
        grid.Children.Add(_eyeHost);
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
        ToolTip.SetTip(_eyeHost, string.IsNullOrEmpty(tip) ? null : tip);
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
