using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui;

/// <summary>程序列表行：列宽与表头共享绝对像素；启停按钮随运行状态切换。</summary>
public sealed class ProgListRowView : ContentView
{
    public static double[]? SharedColWidths;

    private readonly Grid _grid;
    private readonly Button _toggleBtn;
    private UserProgram? _bound;

    public ProgListRowView()
    {
        var check = new CheckBox { VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
        check.SetBinding(CheckBox.IsCheckedProperty, nameof(UserProgram.IsSelected));

        var name = new Label { VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(UserProgram.Name));

        var lang = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        lang.SetBinding(Label.TextProperty, nameof(UserProgram.LanguageText));

        var size = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center };
        size.SetBinding(Label.TextProperty, nameof(UserProgram.SizeText));

        var status = new Label { FontSize = 12, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        status.SetBinding(Label.TextProperty, nameof(UserProgram.StatusText));

        _toggleBtn = new Button
        {
            Text = "▶",
            FontSize = 13,
            TextColor = Color.FromArgb("#059669"),
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            WidthRequest = 28,
            HeightRequest = 28,
            Padding = 0
        };
        _toggleBtn.Clicked += async (_, _) =>
        {
            if (BindingContext is UserProgram prog && FindMainPage() is { } page)
                await page.ProgRowToggleAsync(prog);
        };

        var moreBtn = new Button
        {
            Text = "⋮",
            FontSize = 18,
            TextColor = Color.FromArgb("#6B7280"),
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            WidthRequest = 28,
            HeightRequest = 28,
            Padding = 0
        };
        moreBtn.Clicked += (_, _) =>
        {
            if (BindingContext is UserProgram prog && FindMainPage() is { } page)
                page.ProgRowMoreAsync(prog, moreBtn);
        };

        var actions = new HorizontalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children = { _toggleBtn, moreBtn }
        };

        _grid = new Grid
        {
            Padding = new Thickness(4, 6),
            ColumnSpacing = 4,
            MinimumHeightRequest = 36
        };
        _grid.Add(check, 0);
        _grid.Add(name, 1);
        _grid.Add(lang, 2);
        _grid.Add(size, 3);
        _grid.Add(status, 4);
        _grid.Add(actions, 5);
        Content = _grid;
        Loaded += (_, _) => ApplySharedWidths();
        BindingContextChanged += OnBindingContextChanged;
    }

    private void OnBindingContextChanged(object? sender, EventArgs e)
    {
        if (_bound != null)
            _bound.PropertyChanged -= OnProgPropertyChanged;
        _bound = BindingContext as UserProgram;
        if (_bound != null)
            _bound.PropertyChanged += OnProgPropertyChanged;
        ApplyToggleVisual();
    }

    private void OnProgPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(UserProgram.RuntimeStatus)
            or nameof(UserProgram.IsActivelyRunning)
            or nameof(UserProgram.ActionToggleGlyph))
            MainThread.BeginInvokeOnMainThread(ApplyToggleVisual);
    }

    private void ApplyToggleVisual()
    {
        var running = _bound?.IsActivelyRunning == true;
        _toggleBtn.Text = running ? "■" : "▶";
        _toggleBtn.TextColor = Color.FromArgb(running ? "#DC2626" : "#059669");
        // 勿在状态切换时改 ToolTip：Windows WinRT MapToolTip 会抛 HR 异常
    }

    public void ApplySharedWidths()
    {
        if (SharedColWidths is not { Length: 6 }) return;
        _grid.ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(SharedColWidths[0])),
            new(new GridLength(SharedColWidths[1])),
            new(new GridLength(SharedColWidths[2])),
            new(new GridLength(SharedColWidths[3])),
            new(new GridLength(SharedColWidths[4])),
            new(new GridLength(SharedColWidths[5])),
        };
        // 与表头一致：语言始终显示；仅大小列可在窄屏隐藏
        if (_grid.Children.Count >= 6)
        {
            if (_grid.Children[2] is View lang) lang.IsVisible = true;
            if (_grid.Children[3] is View size) size.IsVisible = SharedColWidths[3] > 1;
        }
    }

    private MainPage? FindMainPage()
    {
        Element? e = this;
        while (e != null)
        {
            if (e is MainPage p) return p;
            e = e.Parent;
        }
        return null;
    }
}
