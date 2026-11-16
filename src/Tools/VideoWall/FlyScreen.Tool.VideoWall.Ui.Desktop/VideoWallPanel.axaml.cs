using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

public partial class VideoWallPanel : UserControl
{
    private enum PanelMode { Sources, Wall, Playback }
    private enum BrowseView { Card, List, Canvas }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<DesktopSourceRow> _rows = new();
    private readonly Dictionary<int, VideoWallFrameSurface> _tileSurfaces = new();
    private PanelMode _mode = PanelMode.Sources;
    private BrowseView _browseView = BrowseView.List;
    private bool _suppressCarouselToggle;
    private readonly DispatcherTimer _carouselTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(WallCarousel.DefaultIntervalMs)
    };

    public Action<string>? StatusReporter
    {
        get => _statusReporter;
        set
        {
            _statusReporter = value;
            Player.StatusReporter = value;
        }
    }

    private Action<string>? _statusReporter;

    public VideoWallPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IVideoWallService>();
        SourcesList.ItemsSource = _rows;
        Player.Closed += OnPlayerClosed;
        Player.RequestInAppMaximize += OnPlayerRequestInAppMaximize;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SearchBox.TextChanged += (_, _) =>
        {
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };
        _carouselTimer.Tick += async (_, _) =>
        {
            if (_service is null || _mode != PanelMode.Wall || !_service.CarouselEnabled) return;
            await _service.AdvanceCarouselAsync();
            await PresentCarouselPageAsync();
            UpdateCarouselChrome();
        };
        ApplyLoc();

        if (_service is null)
            StatusBar.Text = Loc.T("Tool.VideoWall.BackendUnavailable");
        else if (!_service.Backend.IsAvailable)
            StatusBar.Text = Loc.T("Tool.VideoWall.BackendUnavailable");

        RefreshSources();
    }

    private void ApplyLoc()
    {
        ViewCardBtn.Content = Loc.T("Tool.VideoWall.ViewCard");
        ViewListBtn.Content = Loc.T("Tool.VideoWall.ViewList");
        ViewCanvasBtn.Content = Loc.T("Tool.VideoWall.ViewCanvas");
        AddBtn.Content = Loc.T("Tool.VideoWall.Add");
        DeleteBtn.Content = Loc.T("Tool.VideoWall.Delete");
        SearchBox.Watermark = Loc.T("Tool.VideoWall.Search");
        CarouselToggle.Content = Loc.T("Tool.VideoWall.Carousel");
        ToolTip.SetTip(CarouselToggle, Loc.T("Tool.VideoWall.CarouselHint"));
        HdrName.Text = Loc.T("Tool.VideoWall.ColName");
        HdrKind.Text = Loc.T("Tool.VideoWall.ColKind");
        HdrAddress.Text = Loc.T("Tool.VideoWall.Field.Address");
        HdrStatus.Text = Loc.T("Tool.VideoWall.ColStatus");
        HdrActions.Text = Loc.T("Tool.VideoWall.ColActions");
        SyncBrowseChrome();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged += OnChanged;
        _service.SessionChanged += OnChanged;
        Player.AttachService(_service);
        RefreshAll();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _carouselTimer.Stop();
        Player.AttachService(null);
        _ = Player.StopAsync();
        if (_service is null) return;
        _service.CatalogChanged -= OnChanged;
        _service.SessionChanged -= OnChanged;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            _ = RefreshCanvasAsync();
    });

    private void RefreshAll()
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            _ = RefreshCanvasAsync();
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
        Player.IsVisible = mode == PanelMode.Playback;
        if (mode != PanelMode.Playback)
            _ = Player.StopAsync();
        if (mode == PanelMode.Wall)
            RebuildWall();
        SyncBrowseChrome();
    }

    private void SyncBrowseChrome()
    {
        var browsing = _mode is PanelMode.Sources or PanelMode.Wall;
        LayoutButtons.IsVisible = browsing && _browseView == BrowseView.Canvas;
        ViewCardBtn.IsEnabled = browsing;
        ViewListBtn.IsEnabled = browsing;
        ViewCanvasBtn.IsEnabled = browsing;
        _suppressCarouselToggle = true;
        try { CarouselToggle.IsChecked = _service?.CarouselEnabled == true; }
        finally { _suppressCarouselToggle = false; }
        UpdateCarouselChrome();
    }

    private async void ApplyBrowseView(BrowseView view)
    {
        var leavingCanvas = _browseView == BrowseView.Canvas && view != BrowseView.Canvas;
        _browseView = view;
        if (leavingCanvas && _service is not null)
        {
            _carouselTimer.Stop();
            DetachAllTileSurfaces();
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
            WallGrid.Children.Clear();
            _tileSurfaces.Clear();
        }

        if (view == BrowseView.Canvas)
        {
            SetMode(PanelMode.Wall);
            await RefreshCanvasAsync();
        }
        else
            SetMode(PanelMode.Sources);
    }

    private async void OnPlayerClosed()
    {
        if (_browseView == BrowseView.Canvas)
        {
            SetMode(PanelMode.Wall);
            await RefreshCanvasAsync();
        }
        else
            SetMode(PanelMode.Sources);
    }

    private async void CarouselToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressCarouselToggle || _service is null || _mode != PanelMode.Wall) return;
        await _service.SetCarouselEnabledAsync(CarouselToggle.IsChecked == true);
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    private async Task RefreshCanvasAsync()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        _service.SetCarouselSources(_rows.Select(r => r.Id).ToList());
        await _service.ApplyCarouselPageAsync(autoStart: false);
        RebuildWall();
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    private async Task PresentCarouselPageAsync()
    {
        if (_service is null) return;
        for (var i = 0; i < _service.Layout.TileCount; i++)
        {
            if (i >= _service.Tiles.Count) continue;
            var tile = _service.Tiles[i];
            if (tile.SourceId is null || !tile.HasPlayer) continue;
            if (_tileSurfaces.TryGetValue(i, out var surface))
                tile.AttachFrameSink(surface);
            try { await _service.StartTileAsync(i); }
            catch (Exception ex) { StatusReporter?.Invoke($"视频墙格{i}开播失败: {ex.Message}"); }
        }
    }

    private void SyncCarouselTimer()
    {
        if (_service is null || _mode != PanelMode.Wall || !_service.CarouselEnabled || _service.CarouselPageCount <= 1)
        {
            _carouselTimer.Stop();
            return;
        }

        _carouselTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _service.CarouselIntervalMs));
        _carouselTimer.Start();
    }

    private void UpdateCarouselChrome()
    {
        if (_service is null)
        {
            CarouselPageText.Text = "";
            return;
        }

        CarouselPageText.Text = string.Format(
            Loc.T("Tool.VideoWall.CarouselPage"),
            _service.CarouselPage + 1,
            Math.Max(1, _service.CarouselPageCount));
    }

    private void DetachAllTileSurfaces()
    {
        if (_service is not null)
        {
            foreach (var index in _tileSurfaces.Keys.ToList())
            {
                if (index < _service.Tiles.Count)
                {
                    try { _service.Tiles[index].AttachFrameSink(null); }
                    catch { /* ignore */ }
                }
            }
        }

        foreach (var surface in _tileSurfaces.Values)
        {
            try { surface.Clear(); } catch { /* ignore */ }
        }
    }

    private void ViewCardBtn_Click(object? sender, RoutedEventArgs e)
        => ApplyBrowseView(BrowseView.Card);

    private void ViewListBtn_Click(object? sender, RoutedEventArgs e)
        => ApplyBrowseView(BrowseView.List);

    private void ViewCanvasBtn_Click(object? sender, RoutedEventArgs e)
        => ApplyBrowseView(BrowseView.Canvas);

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
        await BeginPlaybackAsync(id);
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
        await RefreshCanvasAsync();
    }

    private void RebuildWall()
    {
        if (_service is null) return;
        DetachAllTileSurfaces();
        WallGrid.Children.Clear();
        _tileSurfaces.Clear();
        var layout = _service.Layout;
        // 应用内最大化：只渲染聚焦格并铺满（其余格由服务层暂停）
        var focus = _service.FocusedTileIndex;
        var focused = focus is int f && f >= 0 && f < layout.TileCount;
        WallGrid.Rows = focused ? 1 : layout.Rows;
        WallGrid.Columns = focused ? 1 : layout.Columns;
        for (var i = 0; i < layout.TileCount; i++)
        {
            if (focused && i != focus) continue;
            var tile = i < _service.Tiles.Count ? _service.Tiles[i] : null;
            var name = tile?.SourceId is { } sid
                ? _service.Catalog.Sources.FirstOrDefault(s => s.Id == sid)?.Name ?? sid
                : Loc.T("Tool.VideoWall.EmptyTile");
            var status = tile?.SourceId is null ? "" : tile.State.ToString();
            var chrome = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(6)
            };
            chrome.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = Avalonia.Media.Brushes.White,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(status))
            {
                chrome.Children.Add(new TextBlock
                {
                    Text = status,
                    Foreground = Avalonia.Media.Brushes.LightGray,
                    FontSize = 11
                });
            }

            var restoreBtn = new Button
            {
                Content = Loc.T("Tool.VideoWall.MaximizeExit"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 6, 0),
                Padding = new Thickness(10, 2),
                IsVisible = focused,
                ZIndex = 3
            };
            restoreBtn.Click += async (_, e) =>
            {
                e.Handled = true;
                await _service.FocusTileAsync(null);
                RebuildWall();
            };

            var cell = new Grid();
            if (tile?.SourceId is not null)
            {
                var image = new Image
                {
                    Stretch = Avalonia.Media.Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                cell.Children.Add(image);
                var surface = new VideoWallFrameSurface(image);
                _tileSurfaces[i] = surface;
                if (tile.HasPlayer)
                    tile.AttachFrameSink(surface);
            }
            else
            {
                cell.Children.Add(new TextBlock
                {
                    Text = Loc.T("Tool.VideoWall.EmptySlot"),
                    Foreground = Avalonia.Media.Brushes.Gray,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(8),
                    IsHitTestVisible = false
                });
            }

            cell.Children.Add(restoreBtn);
            cell.Children.Add(chrome);

            var border = new Border
            {
                Margin = new Thickness(3),
                Background = Avalonia.Media.Brushes.Black,
                Tag = i,
                Child = cell
            };
            border.PointerPressed += Tile_PointerPressed;
            WallGrid.Children.Add(border);
        }
    }

    /// <summary>
    /// 应用内最大化：当前源所在墙格铺满整墙并暂停其它格。
    /// Avalonia 侧墙格暂无视频面，单画面播放保持（音频/状态不中断），不重复起播。
    /// </summary>
    private async void OnPlayerRequestInAppMaximize()
    {
        if (_service is null) return;
        var sourceId = _service.SinglePlayback.CurrentSourceId;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        try
        {
            var tiles = _service.Tiles.ToList();
            var index = tiles.FindIndex(t => t.SourceId == sourceId);
            if (index < 0)
            {
                index = tiles.FindIndex(t => t.SourceId is null);
                if (index < 0) index = 0;
                await _service.BindTileAsync(index, sourceId, autoStart: false);
            }
            else
            {
                index = tiles[index].Index;
            }

            await _service.FocusTileAsync(index);
            _browseView = BrowseView.Canvas;
            SetMode(PanelMode.Wall);
        }
        catch (Exception ex)
        {
            StatusReporter?.Invoke(ex.Message);
        }
    }

    private async void Tile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_service is null || sender is not Border { Tag: int index }) return;
        if (e.ClickCount < 2) return;
        e.Handled = true;

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.SourceId is { } id)
            await BeginPlaybackAsync(id);
    }

    private async void SourcesList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SourcesList.SelectedItem is not DesktopSourceRow row) return;
        await BeginPlaybackAsync(row.Id);
    }

    private async Task BeginPlaybackAsync(string id)
    {
        if (_browseView == BrowseView.Canvas && _mode == PanelMode.Wall && _service is not null)
        {
            _carouselTimer.Stop();
            DetachAllTileSurfaces();
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
            WallGrid.Children.Clear();
            _tileSurfaces.Clear();
        }

        SetMode(PanelMode.Playback);
        var title = ResolvePlaybackTitle(id);
        await Player.PlayAsync(id, title);
    }

    private string ResolvePlaybackTitle(string sourceId)
    {
        var row = _rows.FirstOrDefault(r => r.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(row?.Name))
            return row.Name;
        var src = _service?.Catalog.Sources.FirstOrDefault(s => s.Id == sourceId);
        return !string.IsNullOrWhiteSpace(src?.Name) ? src.Name : sourceId;
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