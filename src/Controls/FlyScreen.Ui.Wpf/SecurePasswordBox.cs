using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FlyScreen.Ui.Theme.Icons;

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
        new PropertyMetadata(string.Empty, OnPlaceholderChanged));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(SecurePasswordBox),
        new PropertyMetadata(false, OnIsRevealedChanged));

    public static readonly DependencyProperty ShowPasswordTipProperty = DependencyProperty.Register(
        nameof(ShowPasswordTip), typeof(string), typeof(SecurePasswordBox),
        new PropertyMetadata(string.Empty, OnTipChanged));

    public static readonly DependencyProperty HidePasswordTipProperty = DependencyProperty.Register(
        nameof(HidePasswordTip), typeof(string), typeof(SecurePasswordBox),
        new PropertyMetadata(string.Empty, OnTipChanged));

    private static readonly Geometry EyeOpenGeometry = Geometry.Parse(IconCatalog.GetPath(IconCodes.EyeOpen));
    private static readonly Geometry EyeOffGeometry = Geometry.Parse(IconCatalog.GetPath(IconCodes.EyeOff));

    private static readonly Brush EyeHoverBrush = CreateFrozenBrush(Color.FromArgb(0x18, 0, 0, 0));

    private readonly PasswordBox _password;
    private readonly TextBox _plain;
    private readonly TextBlock _placeholder;
    private readonly Path _eyePath;
    private readonly Border _eyeHost;
    private bool _syncing;

    public SecurePasswordBox()
    {
        SnapsToDevicePixels = true;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Height = 36;
        Padding = new Thickness(10, 0, 4, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _password = new PasswordBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            PasswordChar = '•',
            Template = CreateBorderlessPasswordTemplate()
        };
        _plain = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed,
            Template = CreateBorderlessTextTemplate()
        };
        _placeholder = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        var inputHost = new Grid();
        inputHost.Children.Add(_placeholder);
        inputHost.Children.Add(_password);
        inputHost.Children.Add(_plain);
        Grid.SetColumn(inputHost, 0);
        grid.Children.Add(inputHost);

        _eyePath = new Path
        {
            Data = EyeOffGeometry,
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16
        };
        // 不用 Button：避免宿主隐式 Button 样式（MinWidth=88 等）污染
        _eyeHost = new Border
        {
            Child = new Viewbox { Width = 16, Height = 16, Child = _eyePath },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        _eyeHost.MouseLeftButtonUp += (_, e) =>
        {
            IsRevealed = !IsRevealed;
            e.Handled = true;
        };
        _eyeHost.MouseEnter += (_, _) => _eyeHost.Background = EyeHoverBrush;
        _eyeHost.MouseLeave += (_, _) => _eyeHost.Background = Brushes.Transparent;
        Grid.SetColumn(_eyeHost, 1);
        grid.Children.Add(_eyeHost);
        Child = grid;

        ApplyThemeFromResources();
        Loaded += (_, _) => ApplyThemeFromResources();

        _password.PasswordChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Password = _password.Password;
            _plain.Text = _password.Password;
            _syncing = false;
            UpdatePlaceholderVisibility();
            RaisePasswordChanged();
        };
        _plain.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Password = _plain.Text ?? "";
            _password.Password = Password;
            _syncing = false;
            UpdatePlaceholderVisibility();
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

    /// <summary>
    /// 构造时尚未挂到父窗，会误吃 Application 深色资源；挂树后/弹窗主题注入后应再刷一次。
    /// </summary>
    public void ApplyThemeFromResources()
    {
        Background = ResolveBrush("ControlBg", Brushes.White);
        BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));
        var textBrush = ResolveBrush("Text", Brushes.Black);
        var muted = ResolveBrush("Placeholder",
            ResolveBrush("Muted", new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))));
        _password.Foreground = textBrush;
        _password.CaretBrush = textBrush;
        _plain.Foreground = textBrush;
        _plain.CaretBrush = textBrush;
        _placeholder.Foreground = muted;
        _eyePath.Fill = muted;
        UpdatePlaceholderVisibility();
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
        _eyeHost.ToolTip = string.IsNullOrEmpty(tip) ? null : tip;
    }

    private void ApplyFocusChrome(bool focused)
    {
        BorderBrush = focused
            ? ResolveBrush("Blue", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)))
            : ResolveBrush("ControlBorder", new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));
    }

    private Brush ResolveBrush(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void UpdatePlaceholderVisibility()
    {
        var empty = string.IsNullOrEmpty(Password);
        _placeholder.Text = Placeholder ?? "";
        _placeholder.Visibility = empty && !string.IsNullOrEmpty(_placeholder.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

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
        box.UpdatePlaceholderVisibility();
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SecurePasswordBox)d).UpdatePlaceholderVisibility();

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
