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
    private readonly DispatcherTimer _fsHideTimer;
    private PanelMode _mode = PanelMode.Sources;
    private BrowseView _browseView = BrowseView.List;
    private string? _pendingPlayId;
    private bool _fullscreen;
    private Window? _fullscreenWindow;
    private bool _volumeApplying;

    public Action<string>? StatusReporter { get; set; }

    public VideoWallPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IVideoWallService>();
        SourcesList.ItemsSource = _rows;
        _fsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _fsHideTimer.Tick += (_, _) => HideFullscreenChrome();
        PlaybackVolumeBar.PropertyChanged += PlaybackVolumeBar_PropertyChanged;
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
        ViewCardBtn.Content = Loc.T("Tool.VideoWall.ViewCard");
        ViewListBtn.Content = Loc.T("Tool.VideoWall.ViewList");
        ViewCanvasBtn.Content = Loc.T("Tool.VideoWall.ViewCanvas");
        AddBtn.Content = Loc.T("Tool.VideoWall.Add");
        DeleteBtn.Content = Loc.T("Tool.VideoWall.Delete");
        SearchBox.Watermark = Loc.T("Tool.VideoWall.Search");
        PlaybackBackBtn.Content = Loc.T("Tool.VideoWall.Back");
        PlaybackStopBtn.Content = Loc.T("Tool.VideoWall.Stop");
        PlaybackMuteBtn.Content = Loc.T("Tool.VideoWall.Mute");
        PlaybackSeekBackBtn.Content = Loc.T("Tool.VideoWall.SeekBack");
        PlaybackSeekFwdBtn.Content = Loc.T("Tool.VideoWall.SeekForward");
        PlaybackPlayPauseBtn.Content = Loc.T("Tool.VideoWall.Pause");
        PlaybackFullscreenBtn.Content = Loc.T("Tool.VideoWall.Fullscreen");
        ToolTip.SetTip(PlaybackVolumeBar, Loc.T("Tool.VideoWall.Volume"));
        VideoPlaceholder.Text = Loc.T("Tool.VideoWall.VideoSurfaceTodo");
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
        RefreshAll();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _fsHideTimer.Stop();
        ExitFullscreen();
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
        SyncBrowseChrome();
    }

    private void SyncBrowseChrome()
    {
        var browsing = _mode is PanelMode.Sources or PanelMode.Wall;
        LayoutButtons.IsVisible = browsing && _browseView == BrowseView.Canvas;
        ViewCardBtn.IsEnabled = browsing;
        ViewListBtn.IsEnabled = browsing;
        ViewCanvasBtn.IsEnabled = browsing;
    }

    private async void ApplyBrowseView(BrowseView view)
    {
        var leavingCanvas = _browseView == BrowseView.Canvas && view != BrowseView.Canvas;
        _browseView = view;
        if (leavingCanvas && _service is not null)
        {
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
            WallGrid.Children.Clear();
        }

        if (view == BrowseView.Canvas)
            SetMode(PanelMode.Wall);
        else
            SetMode(PanelMode.Sources);
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

            var border = new Border
            {
                Margin = new Thickness(3),
                Background = Avalonia.Media.Brushes.Black,
                Tag = i,
                Child = new Grid
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = tile?.SourceId is null
                                ? Loc.T("Tool.VideoWall.EmptyTile")
                                : Loc.T("Tool.VideoWall.DesktopOnly"),
                            Foreground = Avalonia.Media.Brushes.Gray,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Thickness(8),
                            IsHitTestVisible = false
                        },
                        chrome
                    }
                }
            };

            var menu = new ContextMenu();
            foreach (var s in _service.Catalog.Sources)
            {
                var item = new MenuItem { Header = s.Name };
                var sourceId = s.Id;
                var tileIndex = i;
                item.Click += async (_, _) =>
                {
                    await _service.BindTileAsync(tileIndex, sourceId, autoStart: false);
                    RebuildWall();
                    // Desktop 尚无格内视频表面，绑定后进单画面页
                    await PlayAsync(sourceId);
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = Loc.T("Tool.VideoWall.Stop") };
            var clearIndex = i;
            clear.Click += async (_, _) =>
            {
                await _service.ClearTileAsync(clearIndex);
                RebuildWall();
            };
            menu.Items.Add(clear);
            border.ContextMenu = menu;
            border.PointerPressed += Tile_PointerPressed;
            WallGrid.Children.Add(border);
        }
    }

    private async void Tile_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_service is null || sender is not Border { Tag: int index }) return;
        // 单击忽略；双击有源 → 单画面；双击空格 → 打开右键同款菜单
        if (e.ClickCount < 2) return;
        e.Handled = true;

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.SourceId is { } id)
        {
            await PlayAsync(id);
            return;
        }

        if (sender is Control c && c.ContextMenu is { } menu)
            menu.Open(c);
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
        ExitFullscreen();
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        SetMode(PanelMode.Sources);
    }

    private void PlaybackPlayPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var state = _service.SinglePlayback.State;
        if (state == TilePlayState.Paused)
            _service.SinglePlayback.Resume();
        else if (state is TilePlayState.Stopped or TilePlayState.Idle or TilePlayState.Failed)
        {
            var id = _pendingPlayId ?? _service.SinglePlayback.CurrentSourceId;
            if (!string.IsNullOrWhiteSpace(id))
                _ = PlayAsync(id);
        }
        else
            _service.SinglePlayback.Pause();
        BumpFullscreenChrome();
    }

    private async void PlaybackStop_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        await _service.SinglePlayback.StopAsync();
        PlaybackStats.Text = Loc.T("Tool.VideoWall.StatusStopped");
        BumpFullscreenChrome();
    }

    private void PlaybackSeekBack_Click(object? sender, RoutedEventArgs e)
    {
        _service?.SinglePlayback.SeekBy(-10_000);
        BumpFullscreenChrome();
    }

    private void PlaybackSeekFwd_Click(object? sender, RoutedEventArgs e)
    {
        _service?.SinglePlayback.SeekBy(10_000);
        BumpFullscreenChrome();
    }

    private void PlaybackMute_Click(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.ToggleMute();
        SyncVolumeUi();
        BumpFullscreenChrome();
    }

    private void PlaybackVolumeBar_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty || _volumeApplying || _service is null) return;
        _service.SinglePlayback.SetVolume((int)Math.Round(PlaybackVolumeBar.Value));
        SyncMuteLabel();
        BumpFullscreenChrome();
    }

    private void PlaybackFullscreen_Click(object? sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void SyncVolumeUi()
    {
        if (_service is null) return;
        _volumeApplying = true;
        try { PlaybackVolumeBar.Value = _service.SinglePlayback.Volume; }
        finally { _volumeApplying = false; }
        SyncMuteLabel();
    }

    private void SyncMuteLabel()
    {
        if (_service is null) return;
        PlaybackMuteBtn.Content = Loc.T(
            _service.SinglePlayback.IsMuted ? "Tool.VideoWall.Unmute" : "Tool.VideoWall.Mute");
    }

    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host is null) return;

        _fullscreenWindow = host;
        _fullscreen = true;
        host.WindowState = WindowState.FullScreen;
        PlaybackFullscreenBtn.Content = Loc.T("Tool.VideoWall.FullscreenExit");
        host.PointerMoved += Fullscreen_PointerMoved;
        host.KeyDown += Fullscreen_KeyDown;
        BumpFullscreenChrome();
    }

    private void ExitFullscreen()
    {
        _fsHideTimer.Stop();
        var host = _fullscreenWindow;
        _fullscreenWindow = null;
        if (host != null)
        {
            host.PointerMoved -= Fullscreen_PointerMoved;
            host.KeyDown -= Fullscreen_KeyDown;
            if (host.WindowState == WindowState.FullScreen)
                host.WindowState = WindowState.Normal;
        }

        _fullscreen = false;
        PlaybackTransportBar.IsVisible = true;
        PlaybackFullscreenBtn.Content = Loc.T("Tool.VideoWall.Fullscreen");
    }

    private void Fullscreen_PointerMoved(object? sender, PointerEventArgs e)
        => BumpFullscreenChrome();

    private void Fullscreen_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitFullscreen();
            e.Handled = true;
        }
        else BumpFullscreenChrome();
    }

    private void BumpFullscreenChrome()
    {
        if (!_fullscreen) return;
        PlaybackTransportBar.IsVisible = true;
        _fsHideTimer.Stop();
        _fsHideTimer.Start();
    }

    private void HideFullscreenChrome()
    {
        _fsHideTimer.Stop();
        if (_fullscreen)
            PlaybackTransportBar.IsVisible = false;
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
