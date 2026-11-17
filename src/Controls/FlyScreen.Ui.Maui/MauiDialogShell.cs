using FlyScreen.Ui.Theme;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.Ui.Maui;

/// <summary>弹窗尺寸档：表单卡 / 宽内容卡 / 固定理想尺寸（如投影）。</summary>
public enum MauiDialogSize
{
    Form,
    Wide,
    Fixed
}

/// <summary>
/// 全局统一弹窗壳：壳外全透明 + 居中实心卡片（TitleBar / Body / Footer）。
/// App 与工具 UI 共用；各弹窗只填 Body/Footer。
/// </summary>
[ContentProperty(nameof(Body))]
public sealed class MauiDialogShell : ContentView
{
    /// <summary>由宿主主题服务写入，供 <see cref="PrepareOverlayPage"/> 取 ForDialog 色板。</summary>
    public static string ActiveThemeId { get; set; } = ThemePalette.LightName;

    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(MauiDialogShell), "",
            propertyChanged: static (b, _, n) => ((MauiDialogShell)b).OnTitleChanged(n as string));

    public static readonly BindableProperty ShowTitleProperty =
        BindableProperty.Create(nameof(ShowTitle), typeof(bool), typeof(MauiDialogShell), true,
            propertyChanged: static (b, _, n) => ((MauiDialogShell)b)._titleLabel.IsVisible = n is true);

    public static readonly BindableProperty ShowCloseProperty =
        BindableProperty.Create(nameof(ShowClose), typeof(bool), typeof(MauiDialogShell), true,
            propertyChanged: static (b, _, n) => ((MauiDialogShell)b)._closeBtn.IsVisible = n is true);

    public static readonly BindableProperty SizeModeProperty =
        BindableProperty.Create(nameof(SizeMode), typeof(MauiDialogSize), typeof(MauiDialogShell),
            MauiDialogSize.Form);

    public static readonly BindableProperty IdealWidthProperty =
        BindableProperty.Create(nameof(IdealWidth), typeof(double), typeof(MauiDialogShell), 520d);

    public static readonly BindableProperty IdealHeightProperty =
        BindableProperty.Create(nameof(IdealHeight), typeof(double), typeof(MauiDialogShell), 480d);

    public static readonly BindableProperty DismissOnScrimProperty =
        BindableProperty.Create(nameof(DismissOnScrim), typeof(bool), typeof(MauiDialogShell), false);

    public static readonly BindableProperty BodyProperty =
        BindableProperty.Create(nameof(Body), typeof(View), typeof(MauiDialogShell),
            propertyChanged: static (b, _, n) => ((MauiDialogShell)b)._bodyHost.Content = n as View);

    public static readonly BindableProperty FooterProperty =
        BindableProperty.Create(nameof(Footer), typeof(View), typeof(MauiDialogShell),
            propertyChanged: static (b, _, n) => ((MauiDialogShell)b).OnFooterChanged(n as View));

    public static readonly BindableProperty TitleAccessoryProperty =
        BindableProperty.Create(nameof(TitleAccessory), typeof(View), typeof(MauiDialogShell),
            propertyChanged: static (b, _, n) => ((MauiDialogShell)b).OnTitleAccessoryChanged(n as View));

    private readonly Grid _root;
    private readonly Border _card;
    private readonly Border _titleBar;
    private readonly Border _footerBar;
    private readonly Label _titleLabel;
    private readonly Button _closeBtn;
    private readonly Grid _titleGrid;
    private readonly ContentView _titleAccessoryHost;
    private readonly ContentView _bodyHost;
    private readonly ContentView _footerHost;
    private bool _layoutHooked;

    public event EventHandler? CloseRequested;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowTitle
    {
        get => (bool)GetValue(ShowTitleProperty);
        set => SetValue(ShowTitleProperty, value);
    }

    public bool ShowClose
    {
        get => (bool)GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    public MauiDialogSize SizeMode
    {
        get => (MauiDialogSize)GetValue(SizeModeProperty);
        set => SetValue(SizeModeProperty, value);
    }

    public double IdealWidth
    {
        get => (double)GetValue(IdealWidthProperty);
        set => SetValue(IdealWidthProperty, value);
    }

    public double IdealHeight
    {
        get => (double)GetValue(IdealHeightProperty);
        set => SetValue(IdealHeightProperty, value);
    }

    public bool DismissOnScrim
    {
        get => (bool)GetValue(DismissOnScrimProperty);
        set => SetValue(DismissOnScrimProperty, value);
    }

    public View? Body
    {
        get => (View?)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public View? Footer
    {
        get => (View?)GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public View? TitleAccessory
    {
        get => (View?)GetValue(TitleAccessoryProperty);
        set => SetValue(TitleAccessoryProperty, value);
    }

    public Border Card => _card;
    public Border TitleBar => _titleBar;
    public Border FooterBar => _footerBar;
    public Button CloseButton => _closeBtn;

    public MauiDialogShell()
    {
        _titleLabel = new Label
        {
            Style = TryStyle("DlgTitleText"),
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        _titleAccessoryHost = new ContentView
        {
            IsVisible = false,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill
        };

        _closeBtn = new Button
        {
            Text = "✕",
            FontSize = 11,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 46,
            HeightRequest = 36,
            MinimumWidthRequest = 46,
            MinimumHeightRequest = 36,
            Padding = 0,
            Margin = 0,
            CornerRadius = 0,
            BorderWidth = 0,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Fill
        };
        _closeBtn.Clicked += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        ApplyCloseVisualStates(_closeBtn);

        _titleGrid = new Grid
        {
            HeightRequest = 36,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            Children = { _titleLabel, _titleAccessoryHost, _closeBtn }
        };
        Grid.SetColumn(_titleLabel, 0);
        Grid.SetColumn(_titleAccessoryHost, 1);
        Grid.SetColumn(_closeBtn, 2);

        _titleBar = new Border
        {
            Style = TryStyle("DlgTitleBar"),
            StrokeThickness = 0,
            HeightRequest = 36,
            MinimumHeightRequest = 36,
            MaximumHeightRequest = 36,
            Padding = new Thickness(12, 0, 0, 0),
            Content = _titleGrid
        };

        _bodyHost = new ContentView
        {
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill
        };

        _footerHost = new ContentView();
        _footerBar = new Border
        {
            Style = TryStyle("DlgFooter"),
            StrokeThickness = 0,
            Content = _footerHost
        };

        var inner = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            Children = { _titleBar, _bodyHost, _footerBar }
        };
        Grid.SetRow(_titleBar, 0);
        Grid.SetRow(_bodyHost, 1);
        Grid.SetRow(_footerBar, 2);

        _card = new Border
        {
            Style = TryStyle("DlgShell"),
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Padding = 0,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = inner
        };
        if (_card.Style is null)
        {
            _card.BackgroundColor = ColorOf("WinBg") ?? Color.FromArgb("#F7F8FA");
            _card.Stroke = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        }
        _card.GestureRecognizers.Add(new TapGestureRecognizer());

        var scrim = new BoxView
        {
            Color = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) =>
        {
            if (DismissOnScrim)
                CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        scrim.GestureRecognizers.Add(scrimTap);

        _root = new Grid
        {
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(24),
            Children = { scrim, _card }
        };

        BackgroundColor = Colors.Transparent;
        Content = _root;
        SizeChanged += (_, _) => ApplyHostSize(Width, Height);
        Loaded += OnLoaded;
    }

    /// <summary>遮罩弹层页：ForDialog 实心色板 + 壳外全透明。</summary>
    public static void PrepareOverlayPage(ContentPage page, string? themeId = null)
    {
        NavigationPage.SetHasNavigationBar(page, false);
        ApplyForDialogColors(page, themeId ?? ActiveThemeId);
        page.BackgroundColor = Colors.Transparent;
        void KeepClear()
        {
            page.BackgroundColor = Colors.Transparent;
            ClearNativePageBackground(page);
        }
        KeepClear();
        page.Loaded += (_, _) => KeepClear();
        page.HandlerChanged += (_, _) => KeepClear();
        _ = page.Dispatcher.DispatchAsync(async () =>
        {
            foreach (var ms in new[] { 0, 16, 50, 120, 250 })
            {
                if (ms > 0) await Task.Delay(ms);
                KeepClear();
            }
        });
    }

    public static void ApplyForDialogColors(Page page, string? themeId = null)
    {
        var colors = ThemePalette.ForDialog(themeId ?? ActiveThemeId, maui: true);
        foreach (var (key, hex) in ThemePalette.ToResourceMap(colors))
            page.Resources[key] = Color.FromArgb(hex);
    }

    public void ApplyHostSize(double pageW, double pageH)
    {
        if (pageW < 2) pageW = EstimateDisplayWidth();
        if (pageH < 2) pageH = EstimateDisplayHeight();

        var pad = MauiDialogLayout.PagePadding(pageW, pageH);
        _root.Padding = pad;
        var maxW = Math.Max(280, pageW - pad.HorizontalThickness);
        var maxH = Math.Max(240, pageH - pad.VerticalThickness);

        switch (SizeMode)
        {
            case MauiDialogSize.Wide:
                ApplyFluidCard(MauiDialogLayout.ContentMaxWidth(pageW), maxW, maxH);
                break;
            case MauiDialogSize.Fixed:
                ApplyFixedCard(maxW, maxH);
                break;
            default:
                ApplyFluidCard(MauiDialogLayout.FormCardMaxWidth(pageW), maxW, maxH);
                break;
        }
    }

    private void ApplyFluidCard(double preferW, double maxW, double maxH)
    {
        var w = Math.Min(preferW, maxW);
        _card.WidthRequest = w;
        _card.HeightRequest = -1;
        _card.MinimumWidthRequest = Math.Min(280, w);
        _card.MaximumWidthRequest = w;
        _card.MinimumHeightRequest = -1;
        _card.MaximumHeightRequest = maxH;
        _card.VerticalOptions = LayoutOptions.Center;
        _card.HorizontalOptions = LayoutOptions.Center;
    }

    private void ApplyFixedCard(double maxW, double maxH)
    {
        var shellW = Math.Min(IdealWidth, maxW);
        var shellH = Math.Min(IdealHeight, maxH);
        _card.WidthRequest = shellW;
        _card.MaximumWidthRequest = shellW;
        _card.MinimumWidthRequest = Math.Min(320, shellW);
        _card.HeightRequest = shellH;
        _card.MaximumHeightRequest = shellH;
        _card.MinimumHeightRequest = shellH;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_layoutHooked) return;
        if (GetPage() is { } page)
        {
            _layoutHooked = true;
            page.SizeChanged += (_, _) => ApplyHostSize(page.Width, page.Height);
            ApplyHostSize(page.Width, page.Height);
        }
        else
        {
            ApplyHostSize(Width, Height);
        }
    }

    private Page? GetPage()
    {
        Element? e = this;
        while (e != null)
        {
            if (e is Page p) return p;
            e = e.Parent;
        }
        return null;
    }

    private void OnTitleChanged(string? title)
    {
        _titleLabel.Text = title ?? "";
        if (ShowTitle)
            _titleLabel.IsVisible = !string.IsNullOrEmpty(title);
    }

    private void OnTitleAccessoryChanged(View? view)
    {
        _titleAccessoryHost.Content = view;
        _titleAccessoryHost.IsVisible = view != null;
        if (view != null && string.IsNullOrEmpty(Title))
        {
            Grid.SetColumn(_titleAccessoryHost, 0);
            Grid.SetColumnSpan(_titleAccessoryHost, 2);
            _titleLabel.IsVisible = false;
        }
        else
        {
            Grid.SetColumn(_titleAccessoryHost, 1);
            Grid.SetColumnSpan(_titleAccessoryHost, 1);
        }
    }

    private void OnFooterChanged(View? view)
    {
        _footerHost.Content = view;
        _footerBar.IsVisible = view != null;
    }

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;

    private static void ApplyCloseVisualStates(Button btn)
    {
        var groups = new VisualStateGroupList();
        var common = new VisualStateGroup { Name = "CommonStates" };
        common.States.Add(new VisualState
        {
            Name = "Normal",
            Setters =
            {
                new Setter { Property = BackgroundColorProperty, Value = Colors.Transparent }
            }
        });
        common.States.Add(new VisualState
        {
            Name = "PointerOver",
            Setters =
            {
                new Setter { Property = BackgroundColorProperty, Value = Color.FromArgb("#E81123") },
                new Setter { Property = Button.TextColorProperty, Value = Colors.White }
            }
        });
        common.States.Add(new VisualState
        {
            Name = "Pressed",
            Setters =
            {
                new Setter { Property = BackgroundColorProperty, Value = Color.FromArgb("#C50F1F") },
                new Setter { Property = Button.TextColorProperty, Value = Colors.White }
            }
        });
        groups.Add(common);
        VisualStateManager.SetVisualStateGroups(btn, groups);
    }

    private static void ClearNativePageBackground(Page page)
    {
#if WINDOWS || MAUI_WINDOWS
        try
        {
            if (page.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement fe)
                return;
            var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Microsoft.UI.Xaml.DependencyObject? node = fe;
            for (var i = 0; i < 10 && node != null; i++)
            {
                switch (node)
                {
                    case Microsoft.UI.Xaml.Controls.Panel panel:
                        panel.Background = clear;
                        break;
                    case Microsoft.UI.Xaml.Controls.Border border:
                        border.Background = clear;
                        break;
                    case Microsoft.UI.Xaml.Controls.ContentPresenter presenter:
                        presenter.Background = clear;
                        break;
                    case Microsoft.UI.Xaml.Controls.Control control when i == 0:
                        control.Background = clear;
                        break;
                }
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
        }
        catch
        {
            // ignore
        }
#endif
    }

    private static double EstimateDisplayWidth()
    {
        try
        {
            var d = DeviceDisplay.MainDisplayInfo;
            if (d.Width > 0 && d.Density > 0) return d.Width / d.Density;
        }
        catch { /* ignore */ }
        return 520;
    }

    private static double EstimateDisplayHeight()
    {
        try
        {
            var d = DeviceDisplay.MainDisplayInfo;
            if (d.Height > 0 && d.Density > 0) return d.Height / d.Density;
        }
        catch { /* ignore */ }
        return 480;
    }
}
