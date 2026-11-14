using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>添加/编辑规则：半透明遮罩 + 居中卡片。</summary>
internal static class PortProxyRulePrompt
{
    public static async Task<PortProxyRuleInfo?> ShowAsync(
        INavigation? navigation, bool isEdit, PortProxyRuleInfo seed)
    {
        var tcs = new TaskCompletionSource<PortProxyRuleInfo?>();
        var page = new PortProxyRuleCardPage(isEdit, seed, tcs);
        var host = navigation
                   ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (host == null)
        {
            tcs.TrySetResult(null);
            return null;
        }

        await host.PushModalAsync(page, animated: true);
        return await tcs.Task;
    }
}

internal sealed class PortProxyRuleCardPage : ContentPage
{
    private const string FlatEntryStyleId = "PortProxyFlatEntry";
    private readonly TaskCompletionSource<PortProxyRuleInfo?> _tcs;
    private readonly PortProxyDropDown _kindDrop;
    private readonly Entry _listenAddress;
    private readonly Entry _listenPort;
    private readonly Entry _connectAddress;
    private readonly Entry _connectPort;
    private readonly PortProxyDropDown _protocolDrop;
    private string _kindToken;
    private string _protocol;
    private bool _done;

    static PortProxyRuleCardPage()
    {
#if WINDOWS
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping(FlatEntryStyleId, (handler, view) =>
        {
            if (view is not Element el || el.StyleId != FlatEntryStyleId) return;
            if (handler.PlatformView is not Microsoft.UI.Xaml.Controls.TextBox tb) return;
            tb.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            tb.Style = null;
            try
            {
                tb.Resources["TextControlBorderThemeThickness"] = new Microsoft.UI.Xaml.Thickness(0);
                tb.Resources["TextControlBorderThemeThicknessFocused"] = new Microsoft.UI.Xaml.Thickness(0);
                tb.Resources["TextControlBorderThemeThicknessPointerOver"] = new Microsoft.UI.Xaml.Thickness(0);
            }
            catch { /* ignore */ }
        });
#endif
    }

    public PortProxyRuleCardPage(bool isEdit, PortProxyRuleInfo seed, TaskCompletionSource<PortProxyRuleInfo?> tcs)
    {
        _tcs = tcs;
        _kindToken = seed.KindToken;
        _protocol = string.IsNullOrWhiteSpace(seed.Protocol) ? "tcp" : seed.Protocol.Trim();
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromRgba(15, 23, 42, 140);

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        var cardBg = Color.FromArgb(dark ? "#1E293B" : "#FFFFFF");
        var fieldBg = Color.FromArgb(dark ? "#0F172A" : "#FFFFFF");
        var blue = Color.FromArgb("#2563EB");

        var flyoutLayer = new AbsoluteLayout
        {
            IsVisible = false,
            BackgroundColor = Colors.Transparent,
            ZIndex = 200
        };
        var flyout = new PortProxyFlyoutHost(flyoutLayer);

        _kindDrop = new PortProxyDropDown(text, muted, line, fieldBg) { Value = _kindToken };
        _kindDrop.SetOptions("v4tov4", "v4tov6", "v6tov4", "v6tov6");
        _kindDrop.SetEnabled(!isEdit);
        _kindDrop.BindFlyout(flyout);
        _kindDrop.SelectionChanged += v => _kindToken = v;

        _listenAddress = MakeEntry(seed.ListenAddress, text, isEdit);
        _listenPort = MakeEntry(seed.ListenPort, text, isEdit);
        _listenPort.Keyboard = Keyboard.Numeric;
        _connectAddress = MakeEntry(seed.ConnectAddress, text, false);
        _connectPort = MakeEntry(seed.ConnectPort, text, false);
        _connectPort.Keyboard = Keyboard.Numeric;

        _protocolDrop = new PortProxyDropDown(text, muted, line, fieldBg) { Value = _protocol };
        _protocolDrop.SetOptions("tcp");
        _protocolDrop.BindFlyout(flyout);
        _protocolDrop.SelectionChanged += v => _protocol = v;

        var cancel = new Button
        {
            Text = Loc.T("Tool.PortProxy.Cancel"),
            BackgroundColor = fieldBg,
            TextColor = text,
            BorderColor = line,
            BorderWidth = 1,
            CornerRadius = 8,
            HeightRequest = 36,
            Padding = new Thickness(16, 0),
            MinimumWidthRequest = 88
        };
        var ok = new Button
        {
            Text = Loc.T("Tool.PortProxy.Ok"),
            BackgroundColor = blue,
            TextColor = Colors.White,
            CornerRadius = 8,
            HeightRequest = 36,
            Padding = new Thickness(16, 0),
            MinimumWidthRequest = 88
        };
        cancel.Clicked += async (_, _) => await Close(null);
        ok.Clicked += async (_, _) => await OnOk();

        var btnRow = new HorizontalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { cancel, ok }
        };

        var form = new VerticalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.Fill,
            Children =
            {
                new Label
                {
                    Text = Loc.T(isEdit ? "Tool.PortProxy.DialogEdit" : "Tool.PortProxy.DialogAdd"),
                    FontSize = 17,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = text,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                },
                FieldBlock(Loc.T("Tool.PortProxy.Kind"), muted, _kindDrop),
                FieldBlock(Loc.T("Tool.PortProxy.ListenAddress"), muted, _listenAddress),
                FieldBlock(Loc.T("Tool.PortProxy.ListenPort"), muted, _listenPort),
                FieldBlock(Loc.T("Tool.PortProxy.ConnectAddress"), muted, _connectAddress),
                FieldBlock(Loc.T("Tool.PortProxy.ConnectPort"), muted, _connectPort),
                FieldBlock(Loc.T("Tool.PortProxy.Protocol"), muted, _protocolDrop),
                btnRow
            }
        };

        var card = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = cardBg,
            Padding = new Thickness(20, 18),
            WidthRequest = 420,
            MaximumWidthRequest = 440,
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
            Content = form
        };

        var pageBody = new Grid { Padding = new Thickness(24) };
        var dimTap = new TapGestureRecognizer();
        dimTap.Tapped += async (_, _) =>
        {
            _kindDrop.Close();
            _protocolDrop.Close();
            await Close(null);
        };
        pageBody.GestureRecognizers.Add(dimTap);
        pageBody.Add(card);
        card.GestureRecognizers.Add(new TapGestureRecognizer());

        var shell = new Grid();
        shell.Add(pageBody);
        shell.Add(flyoutLayer);
        Content = shell;
    }

    private static View FieldBlock(string label, Color muted, View control)
        => new VerticalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Fill,
            Children =
            {
                new Label
                {
                    Text = label,
                    FontSize = 12,
                    TextColor = muted
                },
                control
            }
        };

    private static Entry MakeEntry(string? text, Color fg, bool readOnly)
        => new()
        {
            Text = text ?? "",
            TextColor = fg,
            BackgroundColor = Colors.Transparent,
            IsReadOnly = readOnly,
            FontSize = 13,
            HeightRequest = 36,
            MinimumHeightRequest = 36,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0),
            StyleId = FlatEntryStyleId
        };

    private async Task OnOk()
    {
        var listenPort = _listenPort.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(listenPort)
            || !int.TryParse(listenPort, out var p)
            || p is < 1 or > 65535)
        {
            await DisplayAlertAsync(
                Loc.T("Tool.PortProxy.DialogAdd"),
                Loc.T("Tool.PortProxy.ValidationPort"),
                Loc.T("Tool.PortProxy.Ok"));
            return;
        }

        var connectAddress = _connectAddress.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(connectAddress))
        {
            await DisplayAlertAsync(
                Loc.T("Tool.PortProxy.DialogAdd"),
                Loc.T("Tool.PortProxy.ValidationConnect"),
                Loc.T("Tool.PortProxy.Ok"));
            return;
        }

        var kind = _kindToken.ToLowerInvariant() switch
        {
            "v4tov6" => PortProxyMappingKind.V4ToV6,
            "v6tov4" => PortProxyMappingKind.V6ToV4,
            "v6tov6" => PortProxyMappingKind.V6ToV6,
            _ => PortProxyMappingKind.V4ToV4
        };
        var connectPort = _connectPort.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(connectPort))
            connectPort = listenPort;

        await Close(new PortProxyRuleInfo
        {
            Kind = kind,
            ListenAddress = _listenAddress.Text?.Trim() ?? "",
            ListenPort = listenPort,
            ConnectAddress = connectAddress,
            ConnectPort = connectPort,
            Protocol = string.IsNullOrWhiteSpace(_protocol) ? "tcp" : _protocol
        });
    }

    private async Task Close(PortProxyRuleInfo? result)
    {
        if (_done) return;
        _done = true;
        _tcs.TrySetResult(result);
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = Close(null);
        return true;
    }
}
