using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public partial class VideoWallPanel : UserControl
{
    private enum PanelMode { Sources, Wall, Playback }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<DesktopSourceRow> _rows = new();
    private PanelMode _mode = PanelMode.Sources;
    private string? _pendingPlayId;

    public Action<string>? StatusReporter { get; set; }

    public VideoWallPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IVideoWallService>();
        SourcesList.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SearchBox.TextChanged += (_, _) => RefreshSources();
        ApplyLoc();

        if (_service is null)
            StatusBar.Text = Loc.T("Tool.VideoWall.BackendUnavailable");
        else if (!_service.Backend.IsAvailable)
            StatusBar.Text = Loc.T("Tool.VideoWall.BackendUnavailable");

        RefreshSources();
    }

    private void ApplyLoc()
    {
        ModeSourcesBtn.Content = Loc.T("Tool.VideoWall.Sources");
        ModeWallBtn.Content = Loc.T("Tool.VideoWall.Wall");
        AddBtn.Content = Loc.T("Tool.VideoWall.Add");
        DeleteBtn.Content = Loc.T("Tool.VideoWall.Delete");
        SearchBox.Watermark = Loc.T("Tool.VideoWall.Search");
        PlaybackBackBtn.Content = Loc.T("Tool.VideoWall.Back");
        VideoPlaceholder.Text = Loc.T("Tool.VideoWall.VideoSurfaceTodo");
        HdrName.Text = Loc.T("Tool.VideoWall.ColName");
        HdrKind.Text = Loc.T("Tool.VideoWall.ColKind");
        HdrAddress.Text = Loc.T("Tool.VideoWall.Field.Address");
        HdrStatus.Text = Loc.T("Tool.VideoWall.ColStatus");
        HdrActions.Text = Loc.T("Tool.VideoWall.ColActions");
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged += OnChanged;
        _service.SessionChanged += OnChanged;
        RefreshAll();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged -= OnChanged;
        _service.SessionChanged -= OnChanged;
    }

    private void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAll);

    private void RefreshAll()
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            RebuildWall();
    }

    private void RefreshSources()
    {
        if (_service is null) return;
        var q = (SearchBox.Text ?? "").Trim();
        _rows.Clear();
        foreach (var s in _service.Catalog.Sources)
        {
            if (q.Length > 0 &&
                !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !s.Endpoint.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            _rows.Add(new DesktopSourceRow
            {
                Id = s.Id,
                Name = s.Name,
                KindText = KindLabel(s.Kind),
                AddressRedacted = VideoEndpointParser.RedactAddress(s.Endpoint.Address),
                StatusText = ValidationLabel(s.ValidationState)
            });
        }
    }

    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        SourcesPane.IsVisible = mode == PanelMode.Sources;
        WallGrid.IsVisible = mode == PanelMode.Wall;
        PlaybackPane.IsVisible = mode == PanelMode.Playback;
        if (mode == PanelMode.Wall)
            RebuildWall();
    }

    private void ModeSourcesBtn_Click(object? sender, RoutedEventArgs e) => SetMode(PanelMode.Sources);
    private void ModeWallBtn_Click(object? sender, RoutedEventArgs e) => SetMode(PanelMode.Wall);

    private async void AddBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var win = new AddSourceWindow(_service);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
        if (win.Saved)
            RefreshAll();
    }

    private async void DeleteBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var rows = SourcesList.SelectedItems?.OfType<DesktopSourceRow>().ToList() ?? [];
        if (rows.Count == 0) return;
        foreach (var row in rows)
            await _service.RemoveSourceAsync(row.Id);
        RefreshAll();
    }

    private async void RowPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        await PlayAsync(id);
    }

    private async void RowEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button { Tag: string id }) return;
        var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == id);
        if (src is null) return;
        var win = new AddSourceWindow(_service, src);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
        if (win.Saved)
            RefreshAll();
    }

    private async void RowDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button { Tag: string id }) return;
        await _service.RemoveSourceAsync(id);
        RefreshAll();
    }

    private async void LayoutBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out var edge)) return;
        var layout = WallLayout.FromEdge(edge);
        if (layout is null) return;
        await _service.SetLayoutAsync(layout);
        RebuildWall();
    }

    private void RebuildWall()
    {
        if (_service is null) return;
        WallGrid.Children.Clear();
        var layout = _service.Layout;
        WallGrid.Rows = layout.Rows;
        WallGrid.Columns = layout.Columns;
        for (var i = 0; i < layout.TileCount; i++)
        {
            var tile = i < _service.Tiles.Count ? _service.Tiles[i] : null;
            var name = tile?.SourceId is { } sid
                ? _service.Catalog.Sources.FirstOrDefault(s => s.Id == sid)?.Name ?? sid
                : Loc.T("Tool.VideoWall.EmptyTile");
            WallGrid.Children.Add(new Border
            {
                Margin = new Thickness(3),
                Background = Avalonia.Media.Brushes.Black,
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = Avalonia.Media.Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextAlignment = Avalonia.Media.TextAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(4)
                }
            });
        }
    }

    private async void SourcesList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (SourcesList.SelectedItem is not DesktopSourceRow row) return;
        await PlayAsync(row.Id);
    }

    private async Task PlayAsync(string id)
    {
        _pendingPlayId = id;
        SetMode(PanelMode.Playback);
        PlaybackStats.Text = Loc.T("Tool.VideoWall.StatusConnecting");
        if (_service is null) return;
        try
        {
            await _service.SinglePlayback.StopAsync();
            await _service.SinglePlayback.PlaySourceAsync(id);
            PlaybackStats.Text = Loc.T("Tool.VideoWall.Play");
            StatusReporter?.Invoke(PlaybackStats.Text);
        }
        catch (Exception ex)
        {
            PlaybackStats.Text = ex.Message;
        }
    }

    private async void PlaybackBackBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        SetMode(PanelMode.Sources);
    }

    private static string KindLabel(VideoSourceKind kind) => kind switch
    {
        VideoSourceKind.Camera => Loc.T("Tool.VideoWall.Type.Camera"),
        VideoSourceKind.NetworkUrl => Loc.T("Tool.VideoWall.Type.Network"),
        VideoSourceKind.LocalFile => Loc.T("Tool.VideoWall.Type.LocalFile"),
        VideoSourceKind.NvrChannel => Loc.T("Tool.VideoWall.Type.Nvr"),
        _ => kind.ToString()
    };

    private static string ValidationLabel(SourceValidationState v) => v switch
    {
        SourceValidationState.Ok => "OK",
        SourceValidationState.Failed => "Failed",
        _ => "—"
    };

    private sealed class DesktopSourceRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string KindText { get; init; }
        public required string AddressRedacted { get; init; }
        public required string StatusText { get; init; }
        public string PlayLabel => Loc.T("Tool.VideoWall.Play");
        public string EditLabel => Loc.T("Tool.VideoWall.Editing");
        public string DeleteLabel => Loc.T("Tool.VideoWall.Delete");
    }
}
