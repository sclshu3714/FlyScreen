using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>添加/编辑源：主窗 PushModal + MauiDialogShell。</summary>
public sealed class VideoWallSourceDialogPage : ContentPage
{
    private readonly IVideoWallService _service;
    private readonly string? _editingId;
    private readonly VideoWallSourceForm _sourceForm;
    private readonly Label _formError;
    private readonly VideoWallFlyoutHost _flyout;
    private readonly AbsoluteLayout _flyoutLayer;
    private bool _closing;

    public event Action? Saved;

    public VideoWallSourceDialogPage(IVideoWallService service, string? editingId)
    {
        _service = service;
        _editingId = editingId;
        MauiDialogShell.PrepareOverlayPage(this);
        Title = editingId is null
            ? Loc.T("Tool.VideoWall.AddSource")
            : Loc.T("Tool.VideoWall.Editing");

        var text = ColorOf("Text") ?? Colors.Black;
        var muted = ColorOf("Muted") ?? Color.FromArgb("#6B7280");
        var line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        var card = ColorOf("CardBg") ?? Colors.White;
        var blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");

        _flyoutLayer = new AbsoluteLayout
        {
            IsVisible = false,
            BackgroundColor = Colors.Transparent,
            ZIndex = 200
        };
        _flyout = new VideoWallFlyoutHost(_flyoutLayer);

        _sourceForm = new VideoWallSourceForm(
            _flyout,
            () => _service.Catalog.Groups.Select(g => g.Name).ToList(),
            text, muted, line, card)
        {
            Padding = new Thickness(0, 0, 20, 4)
        };
        _formError = new Label { TextColor = Color.FromArgb("#DC2626"), FontSize = 12, IsVisible = false };

        if (editingId is not null)
        {
            var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == editingId);
            if (src is not null)
                _sourceForm.BeginEdit(src, _service.Credentials);
            else
                _sourceForm.BeginAdd();
        }
        else
            _sourceForm.BeginAdd();

        var saveBtn = new Button
        {
            Text = Loc.T("Tool.VideoWall.Save"),
            Style = TryStyle("DlgPrimaryBtn"),
            BackgroundColor = blue,
            TextColor = Colors.White
        };
        saveBtn.Clicked += async (_, _) => await SaveAsync();
        var cancelBtn = new Button
        {
            Text = Loc.T("Tool.VideoWall.Cancel"),
            Style = TryStyle("DlgNormalBtn")
        };
        cancelBtn.Clicked += async (_, _) => await CloseAsync();

        var shell = new MauiDialogShell
        {
            Title = Title,
            ShowClose = true,
            SizeMode = MauiDialogSize.Form,
            Body = new ScrollView
            {
                Padding = new Thickness(20, 16, 20, 8),
                Content = new VerticalStackLayout
                {
                    Spacing = 8,
                    Children = { _sourceForm, _formError }
                }
            },
            Footer = new HorizontalStackLayout
            {
                Spacing = 0,
                HorizontalOptions = LayoutOptions.End,
                Children = { cancelBtn, saveBtn }
            }
        };
        shell.CloseRequested += async (_, _) => await CloseAsync();

        AbsoluteLayout.SetLayoutBounds(shell, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(shell, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        AbsoluteLayout.SetLayoutBounds(_flyoutLayer, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(_flyoutLayer, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);

        Content = new AbsoluteLayout
        {
            BackgroundColor = Colors.Transparent,
            Children = { shell, _flyoutLayer }
        };
    }

    public static async Task ShowAsync(IVideoWallService service, string? editingId, Action? onSaved = null)
    {
        var page = new VideoWallSourceDialogPage(service, editingId);
        if (onSaved != null)
            page.Saved += onSaved;
        var nav = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (nav == null) return;
        await nav.PushModalAsync(page, animated: true);
    }

    private async Task SaveAsync()
    {
        _formError.IsVisible = false;
        _flyout.Close();

        var kind = _editingId is not null
            ? _service.Catalog.Sources.FirstOrDefault(s => s.Id == _editingId)?.Kind ?? _sourceForm.Kind
            : _sourceForm.Kind;

        var descriptor = SourceDescriptorBuilder.TryBuild(kind, _sourceForm.Values, _service.Credentials, out var err);
        if (descriptor is null)
        {
            _formError.Text = err ?? "无法保存";
            _formError.IsVisible = true;
            return;
        }

        if (_editingId is not null)
            descriptor = descriptor with { Id = _editingId };

        _service.Credentials.Save();
        var result = _editingId is null
            ? await _service.AddSourceAsync(descriptor)
            : await _service.UpdateSourceAsync(descriptor);

        if (!result.Added)
        {
            _formError.Text = result.Reason;
            _formError.IsVisible = true;
            return;
        }

        _ = await _service.RefreshCoverAsync(descriptor);
        Saved?.Invoke();
        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _flyout.Close();
        try { await Navigation.PopModalAsync(); }
        catch { /* ignore */ }
    }

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;
}
