using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FlyScreen.Ui.Wpf;

/// <summary>
/// 独立安全密码框：单边框内左侧输入、右侧眼睛显隐。文案/主题由使用方或宿主资源提供。
/// </summary>
public sealed class SecurePasswordBox : Border
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(SecurePasswordBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SecurePasswordBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(SecurePasswordBox),
        new PropertyMetadata(false, OnIsRevealedChanged));

    public static readonly DependencyProperty ShowPasswordTipProperty = DependencyProperty.Register(
        nameof(ShowPasswordTip), typeof(string), typeof(SecurePasswordBox),
        new PropertyMetadata(string.Empty, OnTipChanged));

    public static readonly DependencyProperty HidePasswordTipProperty = DependencyProperty.Register(
        nameof(HidePasswordTip), typeof(string), typeof(SecurePasswordBox),
        new PropertyMetadata(string.Empty, OnTipChanged));

    private static readonly Geometry EyeOpenGeometry = Geometry.Parse(
        "M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z");

    private static readonly Geometry EyeOffGeometry = Geometry.Parse(
        "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z");

    private readonly PasswordBox _password;
    private readonly TextBox _plain;
    private readonly Path _eyePath;
    private readonly Button _eyeBtn;
    private bool _syncing;

    public SecurePasswordBox()
    {
        SnapsToDevicePixels = true;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Height = 36;
        Padding = new Thickness(10, 0, 4, 0);
        Background = ResolveBrush("ControlBg", Brushes.White);
        BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBrush = ResolveBrush("Text", Brushes.Black);
        _password = new PasswordBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            PasswordChar = '•',
            Foreground = textBrush,
            CaretBrush = textBrush,
            Template = CreateBorderlessPasswordTemplate()
        };
        _plain = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed,
            Foreground = textBrush,
            CaretBrush = textBrush,
            Template = CreateBorderlessTextTemplate()
        };

        var inputHost = new Grid();
        inputHost.Children.Add(_password);
        inputHost.Children.Add(_plain);
        Grid.SetColumn(inputHost, 0);
        grid.Children.Add(inputHost);

        _eyePath = new Path
        {
            Data = EyeOffGeometry,
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            Fill = ResolveBrush("Muted", new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)))
        };
        _eyeBtn = new Button
        {
            Content = new Viewbox { Width = 16, Height = 16, Child = _eyePath },
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        _eyeBtn.Click += (_, _) => IsRevealed = !IsRevealed;
        Grid.SetColumn(_eyeBtn, 1);
        grid.Children.Add(_eyeBtn);
        Child = grid;

        _password.PasswordChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Password = _password.Password;
            _plain.Text = _password.Password;
            _syncing = false;
            RaisePasswordChanged();
        };
        _plain.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Password = _plain.Text ?? "";
            _password.Password = Password;
            _syncing = false;
            RaisePasswordChanged();
        };

        GotKeyboardFocus += (_, _) => ApplyFocusChrome(true);
        LostKeyboardFocus += (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
                ApplyFocusChrome(false);
        };
        MouseEnter += (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
                BorderBrush = ResolveBrush("Blue", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
        };
        MouseLeave += (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
                BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));
        };
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

    /// <summary>掩码态眼睛按钮提示（由使用方本地化后赋值）。</summary>
    public string ShowPasswordTip
    {
        get => (string)GetValue(ShowPasswordTipProperty);
        set => SetValue(ShowPasswordTipProperty, value ?? "");
    }

    /// <summary>明文态眼睛按钮提示（由使用方本地化后赋值）。</summary>
    public string HidePasswordTip
    {
        get => (string)GetValue(HidePasswordTipProperty);
        set => SetValue(HidePasswordTipProperty, value ?? "");
    }

    public string InputFontFamily
    {
        get => _password.FontFamily?.Source ?? "";
        set
        {
            var family = string.IsNullOrWhiteSpace(value)
                ? SystemFonts.MessageFontFamily
                : new FontFamily(value);
            _password.FontFamily = family;
            _plain.FontFamily = family;
        }
    }

    public double InputFontSize
    {
        get => _password.FontSize;
        set
        {
            _password.FontSize = value;
            _plain.FontSize = value;
        }
    }

    public void FocusInput()
    {
        if (IsRevealed)
        {
            _plain.Focus();
            _plain.CaretIndex = _plain.Text?.Length ?? 0;
        }
        else
            _password.Focus();
    }

    private void RaisePasswordChanged() => PasswordChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyRevealState()
    {
        if (IsRevealed)
        {
            _syncing = true;
            _plain.Text = _password.Password;
            _syncing = false;
            _password.Visibility = Visibility.Collapsed;
            _plain.Visibility = Visibility.Visible;
            _eyePath.Data = EyeOpenGeometry;
            _plain.Focus();
            _plain.CaretIndex = _plain.Text?.Length ?? 0;
        }
        else
        {
            _syncing = true;
            _password.Password = _plain.Text ?? "";
            _syncing = false;
            _plain.Visibility = Visibility.Collapsed;
            _password.Visibility = Visibility.Visible;
            _eyePath.Data = EyeOffGeometry;
        }

        ApplyEyeTip();
    }

    private void ApplyEyeTip()
    {
        var tip = IsRevealed ? HidePasswordTip : ShowPasswordTip;
        _eyeBtn.ToolTip = string.IsNullOrEmpty(tip) ? null : tip;
    }

    private void ApplyFocusChrome(bool focused)
    {
        BorderBrush = focused
            ? ResolveBrush("Blue", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)))
            : ResolveBrush("ControlBorder", new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));
    }

    private Brush ResolveBrush(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SecurePasswordBox)d;
        if (box._syncing) return;
        var value = e.NewValue as string ?? "";
        box._syncing = true;
        if (box._password.Password != value)
            box._password.Password = value;
        if (box._plain.Text != value)
            box._plain.Text = value;
        box._syncing = false;
    }

    private static void OnIsRevealedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SecurePasswordBox)d).ApplyRevealState();

    private static void OnTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SecurePasswordBox)d).ApplyEyeTip();

    private static ControlTemplate CreateBorderlessTextTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var factory = new FrameworkElementFactory(typeof(ScrollViewer));
        factory.Name = "PART_ContentHost";
        factory.SetValue(UIElement.FocusableProperty, false);
        factory.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        factory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = factory;
        return template;
    }

    private static ControlTemplate CreateBorderlessPasswordTemplate()
    {
        var template = new ControlTemplate(typeof(PasswordBox));
        var factory = new FrameworkElementFactory(typeof(ScrollViewer));
        factory.Name = "PART_ContentHost";
        factory.SetValue(UIElement.FocusableProperty, false);
        factory.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        factory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = factory;
        return template;
    }
}
