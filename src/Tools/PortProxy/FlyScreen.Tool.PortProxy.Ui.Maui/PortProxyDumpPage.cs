using FlyScreen.Helpers.Localization;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.PortProxy.Ui.Maui;

/// <summary>netsh 导出文本：主窗 Modal + MauiDialogShell。</summary>
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
        MauiDialogShell.PrepareOverlayPage(this);
        Title = Loc.T("Tool.PortProxy.DialogDump");

        var text = ColorOf("Text") ?? Colors.Black;
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var fieldBg = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");

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
            VerticalOptions = LayoutOptions.Fill,
            HeightRequest = 360,
            MinimumHeightRequest = 200
        };

        _copyBtn = new Button
        {
            Text = Loc.T("Tool.PortProxy.CopyDump"),
            Style = TryStyle("DlgNormalBtn")
        };
        _copyBtn.Clicked += OnCopy;

        var close = new Button
        {
            Text = Loc.T("Tool.PortProxy.Close"),
            Style = TryStyle("DlgPrimaryBtn"),
            BackgroundColor = blue,
            TextColor = Colors.White
        };
        close.Clicked += async (_, _) => await CloseAsync();

        var shell = new MauiDialogShell
        {
            Title = Loc.T("Tool.PortProxy.DialogDump"),
            ShowClose = true,
            SizeMode = MauiDialogSize.Wide,
            Body = new Border
            {
                Stroke = line,
                StrokeThickness = 1,
                BackgroundColor = fieldBg,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = 8,
                Margin = new Thickness(16, 12),
                Content = _editor
            },
            Footer = new HorizontalStackLayout
            {
                Spacing = 0,
                HorizontalOptions = LayoutOptions.End,
                Children = { _copyBtn, close }
            }
        };
        shell.CloseRequested += async (_, _) => await CloseAsync();
        Content = shell;
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
            _copyBtn.Text = Loc.T("Tool.PortProxy.CopyFail");
        }
    }

    private async Task CloseAsync()
    {
        if (_done) return;
        _done = true;
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;
}
