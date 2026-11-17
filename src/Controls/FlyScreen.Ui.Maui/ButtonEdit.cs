using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Storage;
#if MAUI_WINDOWS
using WinRT.Interop;
#endif

namespace FlyScreen.Ui.Maui;

/// <summary>右侧按钮动作：无 / 选文件 / 选文件夹 / 自定义（抛事件）。</summary>
public enum ButtonEditMode
{
    None = 0,
    OpenFile = 1,
    OpenFolder = 2,
    Custom = 3
}

/// <summary>可配置的尾部按钮项（数量可变，模式可切换）。</summary>
public sealed class ButtonEditItem : BindableObject
{
    public static readonly BindableProperty ContentProperty = BindableProperty.Create(
        nameof(Content), typeof(object), typeof(ButtonEditItem), "…");

    public static readonly BindableProperty ModeProperty = BindableProperty.Create(
        nameof(Mode), typeof(ButtonEditMode), typeof(ButtonEditItem), ButtonEditMode.None);

    public static readonly BindableProperty FileFilterProperty = BindableProperty.Create(
        nameof(FileFilter), typeof(string), typeof(ButtonEditItem), "所有文件|*.*");

    public static readonly BindableProperty TipProperty = BindableProperty.Create(
        nameof(Tip), typeof(string), typeof(ButtonEditItem), "");

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

/// <summary>
/// 单边框多按钮编辑框：左侧可编辑（可密码），右侧可配置若干按钮（选文件/选文件夹/自定义）。
/// </summary>
public sealed class ButtonEdit : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(ButtonEdit), string.Empty,
        BindingMode.TwoWay, propertyChanged: OnTextChanged);

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder), typeof(string), typeof(ButtonEdit), string.Empty,
        propertyChanged: OnPlaceholderChanged);

    public static readonly BindableProperty IsPasswordProperty = BindableProperty.Create(
        nameof(IsPassword), typeof(bool), typeof(ButtonEdit), false,
        propertyChanged: OnIsPasswordChanged);

    public static readonly BindableProperty ButtonsProperty = BindableProperty.Create(
        nameof(Buttons), typeof(ObservableCollection<ButtonEditItem>), typeof(ButtonEdit),
        defaultValueCreator: _ => new ObservableCollection<ButtonEditItem>(),
        propertyChanged: OnButtonsChanged);

    private readonly Border _chrome;
    private readonly Entry _entry;
    private readonly HorizontalStackLayout _buttonHost;
    private bool _syncing;
    private ObservableCollection<ButtonEditItem>? _hookedButtons;

    public ButtonEdit()
    {
        var text = ResolveColor("Text", Color.FromArgb("#111827"));
        var muted = ResolveColor("Muted", Color.FromArgb("#6B7280"));
        var line = ResolveColor("ControlBorder", Color.FromArgb("#D1D5DB"));
        var bg = ResolveColor("ControlBg", Colors.White);

        _entry = new Entry
        {
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
            Text = e.NewTextValue ?? "";
            _syncing = false;
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _entry.Focused += (_, _) => ApplyFocusChrome(true);
        _entry.Unfocused += (_, _) => ApplyFocusChrome(false);

        _buttonHost = new HorizontalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };

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
        Grid.SetColumn(_buttonHost, 1);
        row.Children.Add(_buttonHost);

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
        HorizontalOptions = LayoutOptions.Fill;

        HookButtons(Buttons);
        RebuildButtons();
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

    public ObservableCollection<ButtonEditItem> Buttons
    {
        get => (ObservableCollection<ButtonEditItem>)GetValue(ButtonsProperty);
        set => SetValue(ButtonsProperty, value);
    }

    public Entry InnerEntry => _entry;

    private void HookButtons(ObservableCollection<ButtonEditItem>? collection)
    {
        if (_hookedButtons != null)
            _hookedButtons.CollectionChanged -= OnButtonsCollectionChanged;
        _hookedButtons = collection;
        if (_hookedButtons != null)
            _hookedButtons.CollectionChanged += OnButtonsCollectionChanged;
    }

    private void OnButtonsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildButtons();

    private void RebuildButtons()
    {
        _buttonHost.Children.Clear();
        foreach (var item in Buttons)
            _buttonHost.Children.Add(CreateActionHost(item));
    }

    private View CreateActionHost(ButtonEditItem item)
    {
        var blue = ResolveColor("Blue", Color.FromArgb("#2563EB"));
        var label = new Label
        {
            Text = item.Content?.ToString() ?? "…",
            FontSize = 12,
            TextColor = blue,
            VerticalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        var host = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Stroke = Colors.Transparent,
            HeightRequest = 28,
            MinimumWidthRequest = 40,
            Padding = new Thickness(2, 0),
            Margin = new Thickness(2, 0, 0, 0),
            Content = label,
            VerticalOptions = LayoutOptions.Center
        };
        if (!string.IsNullOrWhiteSpace(item.Tip))
            ToolTipProperties.SetText(host, item.Tip);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await ExecuteItemAsync(item).ConfigureAwait(true);
        host.GestureRecognizers.Add(tap);
        return host;
    }

    private async Task ExecuteItemAsync(ButtonEditItem item)
    {
        switch (item.Mode)
        {
            case ButtonEditMode.OpenFile:
            {
                try
                {
                    var types = ParseFileTypes(item.FileFilter);
                    var result = await FilePicker.Default.PickAsync(new PickOptions
                    {
                        PickerTitle = item.Tip is { Length: > 0 } ? item.Tip : "选择文件",
                        FileTypes = types
                    }).ConfigureAwait(true);
                    if (result != null)
                        Text = result.FullPath;
                }
                catch
                {
                    // 用户取消或平台不支持
                }

                break;
            }
            case ButtonEditMode.OpenFolder:
            {
#if MAUI_WINDOWS
                try
                {
                    var path = await PickFolderWindowsAsync(item).ConfigureAwait(true);
                    if (!string.IsNullOrWhiteSpace(path))
                        Text = path;
                }
                catch
                {
                    // 用户取消或平台不支持
                }
#else
                // 非 Windows：由宿主处理（Custom），或后续接平台 FolderPicker
                CustomButtonClick?.Invoke(this, item);
#endif
                break;
            }
            case ButtonEditMode.Custom:
            case ButtonEditMode.None:
            default:
                CustomButtonClick?.Invoke(this, item);
                break;
        }
    }

#if MAUI_WINDOWS
    private static async Task<string?> PickFolderWindowsAsync(ButtonEditItem item)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        if (!string.IsNullOrWhiteSpace(item.Tip))
            picker.CommitButtonText = item.Tip;

        var window = Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView;
        if (window is MauiWinUIWindow winUi)
            InitializeWithWindow.Initialize(picker, winUi.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
#endif

    private static FilePickerFileType ParseFileTypes(string? filter)
    {
        // MAUI FilePickerFileType 按平台；这里用通用扩展名列表
        var exts = new List<string>();
        var parts = (filter ?? "所有文件|*.*").Split('|', StringSplitOptions.TrimEntries);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            foreach (var p in parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var ext = p.Trim().TrimStart('*');
                if (ext is "." or ".*" or "") continue;
                if (!ext.StartsWith(".", StringComparison.Ordinal))
                    ext = "." + ext.TrimStart('.');
                if (!exts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    exts.Add(ext);
            }
        }

        if (exts.Count == 0)
            exts.Add(".*");
        return new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.WinUI, exts },
            { DevicePlatform.Android, exts },
            { DevicePlatform.iOS, exts },
            { DevicePlatform.MacCatalyst, exts }
        });
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

    private static void OnTextChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var box = (ButtonEdit)bindable;
        if (box._syncing) return;
        var text = newValue as string ?? "";
        if (box._entry.Text == text) return;
        box._syncing = true;
        box._entry.Text = text;
        box._syncing = false;
    }

    private static void OnPlaceholderChanged(BindableObject bindable, object oldValue, object newValue)
        => ((ButtonEdit)bindable)._entry.Placeholder = newValue as string ?? "";

    private static void OnIsPasswordChanged(BindableObject bindable, object oldValue, object newValue)
        => ((ButtonEdit)bindable)._entry.IsPassword = newValue is true;

    private static void OnButtonsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var box = (ButtonEdit)bindable;
        box.HookButtons(newValue as ObservableCollection<ButtonEditItem>);
        box.RebuildButtons();
    }
}
