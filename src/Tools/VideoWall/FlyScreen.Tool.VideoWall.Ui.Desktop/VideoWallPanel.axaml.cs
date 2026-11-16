using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
using FlyScreen.Ui.Avalonia;

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
    private bool _suppressCheckSync;
    private bool _carouselPointerOver;
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
        AvaloniaIcon.RegisterTo(Resources);
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
        HdrSelectAll.DoubleTapped += (_, _) =>
        {
            foreach (var row in _rows)
                row.IsSelected = !row.IsSelected;
            SyncListSelectionFromChecks();
            SyncHeaderSelectAll();
        };

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
        ScanBtn.Content = Loc.T("Tool.VideoWall.Scan");
        ToolTip.SetTip(ScanBtn, Loc.T("Tool.VideoWall.Scan"));
        DeleteBtn.Content = Loc.T("Tool.VideoWall.Delete");
        SearchBox.Watermark = Loc.T("Tool.VideoWall.Search");
        CarouselToggle.Content = Loc.T("Tool.VideoWall.Carousel");
        ToolTip.SetTip(CarouselToggle, Loc.T("Tool.VideoWall.CarouselHint"));
        ToolTip.SetTip(CarouselPrevBtn, Loc.T("Tool.VideoWall.CarouselPrev"));
        ToolTip.SetTip(CarouselNextBtn, Loc.T("Tool.VideoWall.CarouselNext"));
        ToolTip.SetTip(HdrSelectAll, Loc.T("Tool.VideoWall.SelectAllTip"));
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

        SyncHeaderSelectAll();
    }

    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        SourcesPane.IsVisible = mode == PanelMode.Sources;
        WallHost.IsVisible = mode == PanelMode.Wall;
        Player.IsVisible = mode == PanelMode.Playback;
        if (mode != PanelMode.Wall)
        {
            _carouselPointerOver = false;
            UpdateCarouselOverlayVisibility();
        }
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
        if (_service is null
            || _mode != PanelMode.Wall
            || !_service.CarouselEnabled
            || _service.CarouselPageCount <= 1
            || _carouselPointerOver)
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
            CarouselDots.Children.Clear();
            UpdateCarouselOverlayVisibility();
            return;
        }

        CarouselPageText.Text = string.Format(
            Loc.T("Tool.VideoWall.CarouselPage"),
            _service.CarouselPage + 1,
            Math.Max(1, _service.CarouselPageCount));
        RebuildCarouselDots();
        UpdateCarouselOverlayVisibility();
    }

    private bool CarouselNavAvailable
        => _service is not null
           && _mode == PanelMode.Wall
           && _service.CarouselEnabled
           && _service.CarouselPageCount > 1;

    private void UpdateCarouselOverlayVisibility()
    {
        var nav = CarouselNavAvailable;
        var showArrows = nav && _carouselPointerOver;
        CarouselPrevBtn.IsVisible = showArrows;
        CarouselNextBtn.IsVisible = showArrows;
        CarouselDots.IsVisible = nav;
    }

    private void RebuildCarouselDots()
    {
        CarouselDots.Children.Clear();
        if (!CarouselNavAvailable || _service is null) return;

        var page = _service.CarouselPage;
        var count = _service.CarouselPageCount;
        // 附件样式：短胶囊 + 当前页加长；色跟主题 Blue / ControlBorder
        var activeBrush = ResolveThemeBrush("Blue");
        var idleBrush = ResolveThemeBrush("ControlBorder");
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var on = i == page;
            var pill = new Border
            {
                Width = on ? 28 : 16,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = on ? activeBrush : idleBrush,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = index
            };
            ToolTip.SetTip(pill, string.Format(Loc.T("Tool.VideoWall.CarouselGoto"), i + 1));
            pill.PointerReleased += CarouselDot_PointerReleased;
            CarouselDots.Children.Add(pill);
        }
    }

    private void WallHost_PointerEntered(object? sender, PointerEventArgs e)
    {
        _carouselPointerOver = true;
        UpdateCarouselOverlayVisibility();
        SyncCarouselTimer();
    }

    private void WallHost_PointerExited(object? sender, PointerEventArgs e)
    {
        _carouselPointerOver = false;
        UpdateCarouselOverlayVisibility();
        SyncCarouselTimer();
    }

    private async void CarouselPrevBtn_Click(object? sender, RoutedEventArgs e)
        => await StepCarouselAsync(next: false);

    private async void CarouselNextBtn_Click(object? sender, RoutedEventArgs e)
        => await StepCarouselAsync(next: true);

    private async void CarouselDot_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control { Tag: int page }) return;
        e.Handled = true;
        await GoToCarouselPageUiAsync(page);
    }

    private async Task StepCarouselAsync(bool next)
    {
        if (_service is null || !CarouselNavAvailable) return;
        if (next) await _service.AdvanceCarouselAsync();
        else await _service.RetreatCarouselAsync();
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    private async Task GoToCarouselPageUiAsync(int page)
    {
        if (_service is null || !CarouselNavAvailable) return;
        if (page == _service.CarouselPage) return;
        await _service.GoToCarouselPageAsync(page);
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
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

    private async void ScanBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var win = new ScanDevicesWindow(_service);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
            await win.ShowDialog(owner);
        else
            win.Show();
        RefreshAll();
    }

    private async void DeleteBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var rows = _rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            rows = SourcesList.SelectedItems?.OfType<DesktopSourceRow>().ToList() ?? [];
        if (rows.Count == 0) return;
        foreach (var row in rows)
            await _service.RemoveSourceAsync(row.Id);
        RefreshAll();
    }

    private void HdrSelectAll_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressCheckSync || sender is not CheckBox cb) return;

        // 单击：全选/取消全选（双击反选见 HdrSelectAll.DoubleTapped）
        var target = cb.IsChecked == true;
        foreach (var row in _rows)
            row.IsSelected = target;

        SyncListSelectionFromChecks();
        SyncHeaderSelectAll();
    }

    private void RowCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressCheckSync) return;
        SyncListSelectionFromChecks();
        SyncHeaderSelectAll();
    }

    private void SourcesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressCheckSync) return;
        _suppressCheckSync = true;
        try
        {
            var selected = SourcesList.SelectedItems?.OfType<DesktopSourceRow>().ToHashSet()
                           ?? new HashSet<DesktopSourceRow>();
            foreach (var row in _rows)
                row.IsSelected = selected.Contains(row);
            SyncHeaderSelectAll();
        }
        finally
        {
            _suppressCheckSync = false;
        }
    }

    private void SyncListSelectionFromChecks()
    {
        if (_suppressCheckSync) return;
        _suppressCheckSync = true;
        try
        {
            SourcesList.SelectedItems?.Clear();
            foreach (var row in _rows.Where(r => r.IsSelected))
                SourcesList.SelectedItems?.Add(row);
        }
        finally
        {
            _suppressCheckSync = false;
        }
    }

    private void SyncHeaderSelectAll()
    {
        _suppressCheckSync = true;
        try
        {
            if (_rows.Count == 0)
            {
                HdrSelectAll.IsChecked = false;
                return;
            }

            var n = _rows.Count(r => r.IsSelected);
            HdrSelectAll.IsChecked = n == 0 ? false : n == _rows.Count ? true : null;
        }
        finally
        {
            _suppressCheckSync = false;
        }
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

                var loading = new LoadingOverlay { SpinnerSize = 28, ZIndex = 2 };
                if (TilePlayStateUi.ShowsLoading(tile.State))
                    loading.Show();
                else
                    loading.Hide();
                cell.Children.Add(loading);
                void OnTileFrame() => Dispatcher.UIThread.Post(loading.Hide);
                surface.FramePresented += OnTileFrame;
                loading.DetachedFromVisualTree += (_, _) => surface.FramePresented -= OnTileFrame;
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
                BorderThickness = new Thickness(1),
                BorderBrush = ResolveThemeBrush("Line"),
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

    private Avalonia.Media.IBrush ResolveThemeBrush(string key)
    {
        if (this.TryGetResource(key, ActualThemeVariant, out var local) && local is Avalonia.Media.IBrush lb)
            return lb;
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var app) == true
            && app is Avalonia.Media.IBrush ab)
            return ab;
        return Avalonia.Media.Brushes.Gray;
    }

    private sealed class DesktopSourceRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string KindText { get; init; }
        public required string AddressRedacted { get; init; }
        public required string StatusText { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public string PlayLabel => Loc.T("Tool.VideoWall.Play");
        public string EditLabel => Loc.T("Tool.VideoWall.Editing");
        public string DeleteLabel => Loc.T("Tool.VideoWall.Delete");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}