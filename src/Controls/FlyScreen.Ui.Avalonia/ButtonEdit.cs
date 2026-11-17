using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FlyScreen.Ui.Avalonia;

/// <summary>右侧按钮动作：无 / 选文件 / 选文件夹 / 自定义（抛事件）。</summary>
public enum ButtonEditMode
{
    None = 0,
    OpenFile = 1,
    OpenFolder = 2,
    Custom = 3
}

/// <summary>可配置的尾部按钮项（数量可变，模式可切换）。</summary>
public sealed class ButtonEditItem : AvaloniaObject
{
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<ButtonEditItem, object?>(nameof(Content), "…");

    public static readonly StyledProperty<ButtonEditMode> ModeProperty =
        AvaloniaProperty.Register<ButtonEditItem, ButtonEditMode>(nameof(Mode), ButtonEditMode.None);

    public static readonly StyledProperty<string> FileFilterProperty =
        AvaloniaProperty.Register<ButtonEditItem, string>(nameof(FileFilter), "所有文件|*.*");

    public static readonly StyledProperty<string> TipProperty =
        AvaloniaProperty.Register<ButtonEditItem, string>(nameof(Tip), "");

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public ButtonEditMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public string FileFilter
    {
        get => GetValue(FileFilterProperty);
        set => SetValue(FileFilterProperty, value ?? "所有文件|*.*");
    }

    public string Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value ?? "");
    }
}

/// <summary>
/// 单边框多按钮编辑框：左侧可编辑（可密码），右侧可配置若干按钮（选文件/选文件夹/自定义）。
/// </summary>
public sealed class ButtonEdit : Border
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ButtonEdit, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<ButtonEdit, string>(nameof(Placeholder), "");

    public static readonly StyledProperty<bool> IsPasswordProperty =
        AvaloniaProperty.Register<ButtonEdit, bool>(nameof(IsPassword), false);

    public static readonly DirectProperty<ButtonEdit, AvaloniaList<ButtonEditItem>> ButtonsProperty =
        AvaloniaProperty.RegisterDirect<ButtonEdit, AvaloniaList<ButtonEditItem>>(
            nameof(Buttons), o => o.Buttons, (o, v) => o.Buttons = v);

    private readonly TextBox _box;
    private readonly StackPanel _buttonHost;
    private AvaloniaList<ButtonEditItem> _buttons = new();
    private bool _syncing;

    public ButtonEdit()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        MinHeight = 36;
        Height = 36;
        MinWidth = 0;
        Padding = new Thickness(10, 0, 4, 0);
        Background = ResolveBrush("ControlBg", Brushes.White);
        BorderBrush = ResolveBrush("ControlBorder", new SolidColorBrush(Color.Parse("#D1D5DB")));

        _box = new TextBox
        {
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
            Text = _box.Text ?? "";
            _syncing = false;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _box.GotFocus += (_, _) => ApplyFocusChrome(true);
        _box.LostFocus += (_, _) => ApplyFocusChrome(false);

        _buttonHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Spacing = 2
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        grid.Children.Add(_box);
        Grid.SetColumn(_buttonHost, 1);
        grid.Children.Add(_buttonHost);
        Child = grid;

        _buttons.CollectionChanged += OnButtonsCollectionChanged;

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

    public event EventHandler? TextChanged;
    public event EventHandler<ButtonEditItem>? CustomButtonClick;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? "");
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value ?? "");
    }

    public bool IsPassword
    {
        get => GetValue(IsPasswordProperty);
        set => SetValue(IsPasswordProperty, value);
    }

    public AvaloniaList<ButtonEditItem> Buttons
    {
        get => _buttons;
        set
        {
            if (ReferenceEquals(_buttons, value)) return;
            _buttons.CollectionChanged -= OnButtonsCollectionChanged;
            _buttons = value ?? new AvaloniaList<ButtonEditItem>();
            _buttons.CollectionChanged += OnButtonsCollectionChanged;
            RaisePropertyChanged(ButtonsProperty, _buttons, _buttons);
            RebuildButtons();
        }
    }

    public TextBox InnerBox => _box;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing)
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
        else if (change.Property == IsPasswordProperty)
        {
            _box.PasswordChar = IsPassword ? '•' : default;
        }
    }

    private void OnButtonsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildButtons();

    private void RebuildButtons()
    {
        _buttonHost.Children.Clear();
        foreach (var item in _buttons)
            _buttonHost.Children.Add(CreateActionHost(item));
    }

    private Border CreateActionHost(ButtonEditItem item)
    {
        var label = new TextBlock
        {
            Text = item.Content?.ToString() ?? "…",
            FontSize = 12,
            Foreground = ResolveBrush("Blue", new SolidColorBrush(Color.Parse("#2563EB"))),
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
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (!string.IsNullOrWhiteSpace(item.Tip))
            ToolTip.SetTip(host, item.Tip);

        var hover = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0));
        host.PointerEntered += (_, _) => host.Background = hover;
        host.PointerExited += (_, _) => host.Background = Brushes.Transparent;
        host.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(host).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            await ExecuteItemAsync(item).ConfigureAwait(true);
        };
        return host;
    }

    private async Task ExecuteItemAsync(ButtonEditItem item)
    {
        switch (item.Mode)
        {
            case ButtonEditMode.OpenFile:
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.StorageProvider is not { } sp) return;
                var filter = ParseFileFilter(item.FileFilter);
                var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = item.Tip is { Length: > 0 } ? item.Tip : "选择文件",
                    AllowMultiple = false,
                    FileTypeFilter = filter
                }).ConfigureAwait(true);
                if (files.Count > 0)
                    Text = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                break;
            }
            case ButtonEditMode.OpenFolder:
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.StorageProvider is not { } sp) return;
                var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = item.Tip is { Length: > 0 } ? item.Tip : "选择文件夹",
                    AllowMultiple = false
                }).ConfigureAwait(true);
                if (folders.Count > 0)
                    Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                break;
            }
            case ButtonEditMode.Custom:
            case ButtonEditMode.None:
            default:
                CustomButtonClick?.Invoke(this, item);
                break;
        }
    }

    private static List<FilePickerFileType> ParseFileFilter(string? filter)
    {
        // "描述|*.pem;*.key|所有文件|*.*"
        var list = new List<FilePickerFileType>();
        var parts = (filter ?? "所有文件|*.*").Split('|', StringSplitOptions.TrimEntries);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.StartsWith("*.", StringComparison.Ordinal) ? p : "*." + p.TrimStart('*', '.'))
                .ToArray();
            list.Add(new FilePickerFileType(parts[i]) { Patterns = patterns });
        }

        if (list.Count == 0)
            list.Add(new FilePickerFileType("所有文件") { Patterns = ["*.*"] });
        return list;
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
