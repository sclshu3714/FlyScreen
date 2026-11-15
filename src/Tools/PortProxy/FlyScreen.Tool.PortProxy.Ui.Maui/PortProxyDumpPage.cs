using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>netsh 导出文本：只读编辑器 + 复制 / 关闭。</summary>
internal static class PortProxyDumpPage
{
    public static async Task ShowAsync(INavigation? navigation, string dumpText)
    {
        var host = navigation
                   ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (host == null) return;
        await host.PushModalAsync(new PortProxyDumpModalPage(dumpText ?? ""), animated: true);
    }
}

internal sealed class PortProxyDumpModalPage : ContentPage
{
    private readonly Editor _editor;
    private readonly Button _copyBtn;
    private bool _done;

    public PortProxyDumpModalPage(string dumpText)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromRgba(15, 23, 42, 140);

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        var cardBg = Color.FromArgb(dark ? "#1E293B" : "#FFFFFF");
        var fieldBg = Color.FromArgb(dark ? "#0F172A" : "#FFFFFF");
        var blue = Color.FromArgb("#2563EB");

        _editor = new Editor
        {
            Text = dumpText,
            IsReadOnly = true,
            FontSize = 12,
            FontFamily = "Consolas",
            TextColor = text,
            BackgroundColor = fieldBg,
            AutoSize = EditorAutoSizeOption.Disabled,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };

        var scroll = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            VerticalScrollBarVisibility = ScrollBarVisibility.Always,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Default,
            Content = _editor,
            HeightRequest = 360,
            MinimumHeightRequest = 200
        };

        _copyBtn = new Button
        {
            Text = Loc.T("Tool.PortProxy.CopyDump"),
            BackgroundColor = fieldBg,
            TextColor = text,
            BorderColor = line,
            BorderWidth = 1,
            CornerRadius = 8,
            HeightRequest = 36,
            Padding = new Thickness(16, 0),
            MinimumWidthRequest = 100
        };
        _copyBtn.Clicked += OnCopy;

        var close = new Button
        {
            Text = Loc.T("Tool.PortProxy.Close"),
            BackgroundColor = blue,
            TextColor = Colors.White,
            CornerRadius = 8,
            HeightRequest = 36,
            Padding = new Thickness(16, 0),
            MinimumWidthRequest = 88
        };
        close.Clicked += async (_, _) => await CloseAsync();

        var title = new Label
        {
            Text = Loc.T("Tool.PortProxy.DialogDump"),
            FontSize = 17,
            FontAttributes = FontAttributes.Bold,
            TextColor = text,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var btnRow = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _copyBtn, close }
        };

        var inner = new VerticalStackLayout
        {
            Spacing = 0,
            Children = { title, scroll, btnRow }
        };

        var card = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = cardBg,
            Padding = new Thickness(20, 18),
            WidthRequest = 640,
            MaximumWidthRequest = 720,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(0, 8),
                Radius = 24,
                Opacity = 0.22f
            },
            Content = inner
        };

        var body = new Grid { Padding = new Thickness(16) };
        var dimTap = new TapGestureRecognizer();
        dimTap.Tapped += async (_, _) => await CloseAsync();
        body.GestureRecognizers.Add(dimTap);
        body.Add(card);
        card.GestureRecognizers.Add(new TapGestureRecognizer());
        Content = body;
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(_editor.Text ?? "");
            _copyBtn.Text = Loc.T("Tool.PortProxy.Copied");
        }
        catch
        {
            /* ignore */
        }
    }

    private async Task CloseAsync()
    {
        if (_done) return;
        _done = true;
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}