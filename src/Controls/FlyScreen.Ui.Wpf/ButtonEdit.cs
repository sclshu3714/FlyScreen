using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using IoPath = System.IO.Path;

namespace FlyScreen.Ui.Wpf;

/// <summary>右侧按钮动作：无 / 选文件 / 选文件夹 / 自定义（抛事件）。</summary>
public enum ButtonEditMode
{
    None = 0,
    OpenFile = 1,
    OpenFolder = 2,
    Custom = 3
}

/// <summary>可配置的尾部按钮项（数量可变，模式可切换）。</summary>
public sealed class ButtonEditItem : DependencyObject
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(ButtonEditItem), new PropertyMetadata("…"));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(ButtonEditMode), typeof(ButtonEditItem),
        new PropertyMetadata(ButtonEditMode.None));

    public static readonly DependencyProperty FileFilterProperty = DependencyProperty.Register(
        nameof(FileFilter), typeof(string), typeof(ButtonEditItem),
        new PropertyMetadata("所有文件|*.*"));

    public static readonly DependencyProperty TipProperty = DependencyProperty.Register(
        nameof(Tip), typeof(string), typeof(ButtonEditItem), new PropertyMetadata(""));

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public ButtonEditMode Mode
    {
        get => (ButtonEditMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public string FileFilter
    {
        get => (string)GetValue(FileFilterProperty);
        set => SetValue(FileFilterProperty, value ?? "所有文件|*.*");
    }

    public string Tip
    {
        get => (string)GetValue(TipProperty);
        set => SetValue(TipProperty, value ?? "");
    }
}

public sealed class ButtonEditItemCollection : FreezableCollection<ButtonEditItem>
{
}

/// <summary>
/// 单边框多按钮编辑框：左侧可编辑（可密码），右侧可配置若干按钮（选文件/选文件夹/自定义）。
/// </summary>
[ContentProperty(nameof(Buttons))]
public sealed class ButtonEdit : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ButtonEdit),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(ButtonEdit), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsPasswordProperty = DependencyProperty.Register(
        nameof(IsPassword), typeof(bool), typeof(ButtonEdit),
        new PropertyMetadata(false, OnIsPasswordChanged));

    public static readonly DependencyProperty ButtonsProperty = DependencyProperty.Register(
        nameof(Buttons), typeof(ButtonEditItemCollection), typeof(ButtonEdit),
        new PropertyMetadata(null, OnButtonsChanged));

    private readonly TextBox _plain;
    private readonly PasswordBox _password;
    private readonly StackPanel _buttonHost;
    private bool _syncing;

    public ButtonEdit()
    {
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Height = 36;
        MinWidth = 0;
        Padding = new Thickness(10, 0, 4, 0);
        Background = ResolveBrush("ControlBg", Brushes.White);
        BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)));

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBrush = ResolveBrush("Text", Brushes.Black);
        _plain = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Foreground = textBrush,
            CaretBrush = textBrush,
            Template = CreateBorderlessTextTemplate()
        };
        _password = new PasswordBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            PasswordChar = '•',
            Visibility = Visibility.Collapsed,
            Foreground = textBrush,
            CaretBrush = textBrush,
            Template = CreateBorderlessPasswordTemplate()
        };

        var inputHost = new Grid();
        inputHost.Children.Add(_plain);
        inputHost.Children.Add(_password);
        Grid.SetColumn(inputHost, 0);
        grid.Children.Add(inputHost);

        _buttonHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(_buttonHost, 1);
        grid.Children.Add(_buttonHost);
        Child = grid;

        SetCurrentValue(ButtonsProperty, new ButtonEditItemCollection());

        _plain.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Text = _plain.Text ?? "";
            _password.Password = Text;
            _syncing = false;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _password.PasswordChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Text = _password.Password;
            _plain.Text = Text;
            _syncing = false;
            TextChanged?.Invoke(this, EventArgs.Empty);
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

    public event EventHandler? TextChanged;
    public event EventHandler<ButtonEditItem>? CustomButtonClick;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? "");
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value ?? "");
    }

    public bool IsPassword
    {
        get => (bool)GetValue(IsPasswordProperty);
        set => SetValue(IsPasswordProperty, value);
    }

    public ButtonEditItemCollection Buttons
    {
        get => (ButtonEditItemCollection)GetValue(ButtonsProperty);
        set => SetValue(ButtonsProperty, value);
    }

    public void FocusInput()
    {
        if (IsPassword)
        {
            _password.Focus();
        }
        else
        {
            _plain.Focus();
            _plain.CaretIndex = _plain.Text?.Length ?? 0;
        }
    }

    private void RebuildButtons()
    {
        _buttonHost.Children.Clear();
        var items = Buttons;
        if (items == null) return;
        foreach (ButtonEditItem item in items)
        {
            var host = CreateActionHost(item);
            _buttonHost.Children.Add(host);
        }
    }

    private Border CreateActionHost(ButtonEditItem item)
    {
        var label = new TextBlock
        {
            Text = item.Content?.ToString() ?? "…",
            FontSize = 12,
            Foreground = ResolveBrush("Blue", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        var host = new Border
        {
            Child = label,
            Height = 28,
            MinWidth = 40,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(2, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = item,
            ToolTip = string.IsNullOrWhiteSpace(item.Tip) ? null : item.Tip
        };
        var hover = CreateFrozenBrush(Color.FromArgb(0x18, 0, 0, 0));
        host.MouseEnter += (_, _) => host.Background = hover;
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, e) =>
        {
            ExecuteItem(item);
            e.Handled = true;
        };
        return host;
    }

    private void ExecuteItem(ButtonEditItem item)
    {
        switch (item.Mode)
        {
            case ButtonEditMode.OpenFile:
            {
                var dlg = new OpenFileDialog
                {
                    Title = item.Tip is { Length: > 0 } ? item.Tip : "选择文件",
                    Filter = string.IsNullOrWhiteSpace(item.FileFilter) ? "所有文件|*.*" : item.FileFilter,
                    CheckFileExists = true
                };
                if (!string.IsNullOrWhiteSpace(Text) && File.Exists(Text))
                    dlg.InitialDirectory = IoPath.GetDirectoryName(Text);
                if (dlg.ShowDialog(Window.GetWindow(this)) == true)
                    Text = dlg.FileName;
                break;
            }
            case ButtonEditMode.OpenFolder:
            {
                var dlg = new OpenFolderDialog
                {
                    Title = item.Tip is { Length: > 0 } ? item.Tip : "选择文件夹"
                };
                if (!string.IsNullOrWhiteSpace(Text) && Directory.Exists(Text))
                    dlg.InitialDirectory = Text;
                if (dlg.ShowDialog(Window.GetWindow(this)) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                    Text = dlg.FolderName;
                break;
            }
            case ButtonEditMode.Custom:
            case ButtonEditMode.None:
            default:
                CustomButtonClick?.Invoke(this, item);
                break;
        }
    }

    private void ApplyPasswordMode()
    {
        if (IsPassword)
        {
            _plain.Visibility = Visibility.Collapsed;
            _password.Visibility = Visibility.Visible;
            _syncing = true;
            _password.Password = Text ?? "";
            _syncing = false;
        }
        else
        {
            _password.Visibility = Visibility.Collapsed;
            _plain.Visibility = Visibility.Visible;
            _syncing = true;
            _plain.Text = Text ?? "";
            _syncing = false;
        }
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

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (ButtonEdit)d;
        if (box._syncing) return;
        var value = e.NewValue as string ?? "";
        box._syncing = true;
        if (box._plain.Text != value)
            box._plain.Text = value;
        if (box._password.Password != value)
            box._password.Password = value;
        box._syncing = false;
    }

    private static void OnIsPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ButtonEdit)d).ApplyPasswordMode();

    private static void OnButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (ButtonEdit)d;
        if (e.OldValue is INotifyCollectionChanged oldCol)
            oldCol.CollectionChanged -= box.OnButtonsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCol)
            newCol.CollectionChanged += box.OnButtonsCollectionChanged;
        box.RebuildButtons();
    }

    private void OnButtonsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildButtons();

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
