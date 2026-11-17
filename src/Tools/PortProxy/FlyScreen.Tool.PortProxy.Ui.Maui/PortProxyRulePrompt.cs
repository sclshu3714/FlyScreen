using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>添加/编辑规则：主窗 Modal + MauiDialogShell（壳外全透明）。</summary>
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
    private readonly Entry _protocolEntry;
    private readonly bool _isEdit;
    private string _kindToken;
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
        _isEdit = isEdit;
        _kindToken = seed.KindToken;
        var protocol = string.IsNullOrWhiteSpace(seed.Protocol) ? "tcp" : seed.Protocol.Trim();
        MauiDialogShell.PrepareOverlayPage(this);

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Color.FromArgb("#6B7280");
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var fieldBg = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");

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

        _protocolEntry = MakeEntry(protocol, text, readOnly: false);

        var cancel = new Button
        {
            Text = Loc.T("Tool.PortProxy.Cancel"),
            Style = TryStyle("DlgNormalBtn")
        };
        var ok = new Button
        {
            Text = Loc.T("Tool.PortProxy.Ok"),
            Style = TryStyle("DlgPrimaryBtn"),
            BackgroundColor = blue,
            TextColor = Colors.White
        };
        cancel.Clicked += async (_, _) => await Close(null);
        ok.Clicked += async (_, _) => await OnOk();

        var dlgShell = new MauiDialogShell
        {
            Title = Loc.T(isEdit ? "Tool.PortProxy.DialogEdit" : "Tool.PortProxy.DialogAdd"),
            ShowClose = true,
            SizeMode = MauiDialogSize.Form,
            Body = new ScrollView
            {
                Padding = new Thickness(20, 16, 20, 8),
                Content = new VerticalStackLayout
                {
                    Spacing = 10,
                    Children =
                    {
                        FieldBlock(Loc.T("Tool.PortProxy.Kind"), muted, _kindDrop),
                        FieldBlock(Loc.T("Tool.PortProxy.ListenAddress"), muted, _listenAddress),
                        FieldBlock(Loc.T("Tool.PortProxy.ListenPort"), muted, _listenPort),
                        FieldBlock(Loc.T("Tool.PortProxy.ConnectAddress"), muted, _connectAddress),
                        FieldBlock(Loc.T("Tool.PortProxy.ConnectPort"), muted, _connectPort),
                        FieldBlock(Loc.T("Tool.PortProxy.Protocol"), muted, _protocolEntry)
                    }
                }
            },
            Footer = new HorizontalStackLayout
            {
                Spacing = 0,
                HorizontalOptions = LayoutOptions.End,
                Children = { cancel, ok }
            }
        };
        dlgShell.CloseRequested += async (_, _) => await Close(null);

        AbsoluteLayout.SetLayoutBounds(dlgShell, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(dlgShell, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        AbsoluteLayout.SetLayoutBounds(flyoutLayer, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(flyoutLayer, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        Content = new AbsoluteLayout
        {
            BackgroundColor = Colors.Transparent,
            Children = { dlgShell, flyoutLayer }
        };
    }

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

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
                DialogTitle(),
                Loc.T("Tool.PortProxy.ValidationPort"),
                Loc.T("Tool.PortProxy.Ok"));
            return;
        }

        var connectAddress = _connectAddress.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(connectAddress))
        {
            await DisplayAlertAsync(
                DialogTitle(),
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

        var protocol = _protocolEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(protocol))
            protocol = "tcp";

        await Close(new PortProxyRuleInfo
        {
            Kind = kind,
            ListenAddress = _listenAddress.Text?.Trim() ?? "",
            ListenPort = listenPort,
            ConnectAddress = connectAddress,
            ConnectPort = connectPort,
            Protocol = protocol
        });
    }

    private string DialogTitle()
        => Loc.T(_isEdit ? "Tool.PortProxy.DialogEdit" : "Tool.PortProxy.DialogAdd");

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
