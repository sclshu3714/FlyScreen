using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public partial class VideoWallPanel : UserControl
{
    private enum PanelMode { Sources, Wall, Playback }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<SourceRowVm> _rows = new();
    private PanelMode _mode = PanelMode.Sources;
    private bool _cardView;
    private string? _pendingPlayId;
    private string _groupFilter = "";
    private bool _suppressCombo;

    public Action<string>? StatusReporter { get; set; }

    public VideoWallPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IVideoWallService>();
        SourcesGrid.ItemsSource = _rows;
        SourcesCards.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SearchBox.TextChanged += (_, _) => RefreshSources();
        GroupFilter.SelectionChanged += (_, _) =>
        {
            var sel = GroupFilter.SelectedItem as string;
            _groupFilter = string.Equals(sel, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) ? "" : sel ?? "";
            RefreshSources();
        };
        ApplyLoc();

        if (_service is null)
        {
            StatusBar.Text = Loc.T("Tool.VideoWall.BackendUnavailable");
            return;
        }

        if (!_service.Backend.IsAvailable)
            StatusBar.Text = Loc.T("Tool.VideoWall.BackendUnavailable");

        SyncCombosFromState();
        RefreshSources();
    }

    private void ApplyLoc()
    {
        _suppressCombo = true;
        try
        {
            ModeCombo.Items.Clear();
            ModeCombo.Items.Add(Loc.T("Tool.VideoWall.Sources"));
            ModeCombo.Items.Add(Loc.T("Tool.VideoWall.Wall"));

            ViewCombo.Items.Clear();
            ViewCombo.Items.Add(Loc.T("Tool.VideoWall.ViewList"));
            ViewCombo.Items.Add(Loc.T("Tool.VideoWall.ViewCard"));

            LayoutCombo.Items.Clear();
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "1×1", Tag = 1 });
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "2×2", Tag = 2 });
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "3×3", Tag = 3 });
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "4×4", Tag = 4 });
        }
        finally
        {
            _suppressCombo = false;
        }

        SearchBox.ToolTip = Loc.T("Tool.VideoWall.Search");
        AddBtn.ToolTip = Loc.T("Tool.VideoWall.Add");
        DeleteBtn.ToolTip = Loc.T("Tool.VideoWall.Delete");
        LayoutCombo.ToolTip = Loc.T("Tool.VideoWall.Layout");
        PlaybackBackBtn.ToolTip = Loc.T("Tool.VideoWall.Back");
        EmptyTitle.Text = Loc.T("Tool.VideoWall.EmptyTitle");
        EmptyHint.Text = Loc.T("Tool.VideoWall.EmptyHint");
        ColName.Header = Loc.T("Tool.VideoWall.ColName");
        ColKind.Header = Loc.T("Tool.VideoWall.ColKind");
        ColAddress.Header = Loc.T("Tool.VideoWall.Field.Address");
        ColGroup.Header = Loc.T("Tool.VideoWall.Field.Group");
        ColStatus.Header = Loc.T("Tool.VideoWall.ColStatus");
        ColActions.Header = Loc.T("Tool.VideoWall.ColActions");
    }

    private void SyncCombosFromState()
    {
        _suppressCombo = true;
        try
        {
            ModeCombo.SelectedIndex = _mode == PanelMode.Wall ? 1 : 0;
            ViewCombo.SelectedIndex = _cardView ? 1 : 0;
            ViewCombo.IsEnabled = _mode == PanelMode.Sources;

            var edge = _service?.Layout.Rows ?? 3;
            for (var i = 0; i < LayoutCombo.Items.Count; i++)
            {
                if (LayoutCombo.Items[i] is ComboBoxItem { Tag: int tag } && tag == edge)
                {
                    LayoutCombo.SelectedIndex = i;
                    break;
                }
            }

            if (LayoutCombo.SelectedIndex < 0 && LayoutCombo.Items.Count > 0)
                LayoutCombo.SelectedIndex = Math.Min(2, LayoutCombo.Items.Count - 1);
        }
        finally
        {
            _suppressCombo = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InheritHostBrushes(Window.GetWindow(this) ?? (FrameworkElement?)Parent);
        if (_service is null) return;
        _service.CatalogChanged += OnCatalogOrSession;
        _service.SessionChanged += OnCatalogOrSession;
        _service.SinglePlayback.StatsChanged += OnStatsChanged;
        _service.SinglePlayback.StateChanged += OnPlaybackState;
        PopulateGroups();
        SyncCombosFromState();
        RefreshAll();
    }

    /// <summary>从宿主窗口拷贝主题色，使工具区与主程序样式一致。</summary>
    private void InheritHostBrushes(FrameworkElement? host)
    {
        if (host is null) return;
        foreach (var key in new[]
                 {
                     "Blue", "BluePress", "BlueSoft", "BlueHover", "Muted", "Line", "Text",
                     "WinBg", "CardBg", "SubtleBg", "ControlBg", "ControlBorder", "ControlHover"
                 })
        {
            if (host.TryFindResource(key) is SolidColorBrush brush)
                Resources[key] = brush.Clone();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged -= OnCatalogOrSession;
        _service.SessionChanged -= OnCatalogOrSession;
        _service.SinglePlayback.StatsChanged -= OnStatsChanged;
        _service.SinglePlayback.StateChanged -= OnPlaybackState;
        _ = _service.SinglePlayback.StopAsync();
    }

    private void OnCatalogOrSession()
        => Dispatcher.BeginInvoke(RefreshAll);

    private void OnStatsChanged(MediaStats stats)
        => Dispatcher.BeginInvoke(() =>
        {
            PlaybackStats.Text =
                $"{stats.Width}×{stats.Height} @ {stats.Fps:F1} fps, {stats.BitrateKbps} kbps, buf {stats.BufferMs} ms";
        });

    private void OnPlaybackState(TilePlayState state)
        => Dispatcher.BeginInvoke(() => StatusBar.Text = StatusTextFor(state));

    private void PopulateGroups()
    {
        if (_service is null) return;
        GroupFilter.Items.Clear();
        GroupFilter.Items.Add(Loc.T("Tool.VideoWall.GroupAll"));
        foreach (var g in _service.Catalog.Groups)
            GroupFilter.Items.Add(g.Name);
        GroupFilter.SelectedIndex = 0;
    }

    private void RefreshAll()
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            RebuildWallGrid();
        SyncCombosFromState();
    }

    private void RefreshSources()
    {
        if (_service is null) return;
        var q = (SearchBox.Text ?? "").Trim();
        _rows.Clear();
        foreach (var s in _service.Catalog.Sources)
        {
            if (!string.IsNullOrEmpty(_groupFilter) &&
                !string.Equals(_groupFilter, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) &&
                !MatchesGroup(s, _groupFilter))
                continue;

            if (q.Length > 0 &&
                !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !s.Endpoint.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            _rows.Add(ToRow(s));
        }

        var empty = _rows.Count == 0;
        EmptyState.Visibility = empty && _mode == PanelMode.Sources ? Visibility.Visible : Visibility.Collapsed;
        SourcesGrid.Visibility = !_cardView && !empty ? Visibility.Visible : Visibility.Collapsed;
        SourcesCards.Visibility = _cardView && !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool MatchesGroup(VideoSourceDescriptor s, string groupName)
        => string.Equals(s.GroupId, groupName, StringComparison.OrdinalIgnoreCase);

    private SourceRowVm ToRow(VideoSourceDescriptor s)
    {
        var coverPath = s.CoverKey is not null && _service!.Covers.Exists(s.CoverKey)
            ? _service.Covers.PathForKey(s.CoverKey)
            : null;

        return new SourceRowVm
        {
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind,
            KindText = KindLabel(s.Kind),
            AddressRedacted = VideoEndpointParser.RedactAddress(s.Endpoint.Address),
            Group = s.GroupId,
            StatusText = ValidationLabel(s.ValidationState),
            CoverPath = coverPath,
            Source = s
        };
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

    private static string StatusTextFor(TilePlayState state) => state switch
    {
        TilePlayState.Connecting => Loc.T("Tool.VideoWall.StatusConnecting"),
        TilePlayState.Buffering => Loc.T("Tool.VideoWall.StatusBuffering"),
        TilePlayState.Stalled => Loc.T("Tool.VideoWall.StatusStalled"),
        TilePlayState.Reconnecting => Loc.T("Tool.VideoWall.StatusReconnecting"),
        TilePlayState.Stopped => Loc.T("Tool.VideoWall.StatusStopped"),
        _ => state.ToString()
    };

    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        MainToolbar.Visibility = mode == PanelMode.Playback ? Visibility.Collapsed : Visibility.Visible;
        SourcesPane.Visibility = mode == PanelMode.Sources ? Visibility.Visible : Visibility.Collapsed;
        WallGrid.Visibility = mode == PanelMode.Wall ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPane.Visibility = mode == PanelMode.Playback ? Visibility.Visible : Visibility.Collapsed;
        ViewCombo.IsEnabled = mode == PanelMode.Sources;
        LayoutCombo.IsEnabled = mode == PanelMode.Wall || mode == PanelMode.Sources;
        if (mode == PanelMode.Wall)
            RebuildWallGrid();
        else if (mode == PanelMode.Sources)
            RefreshSources();
        SyncCombosFromState();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || ModeCombo.SelectedIndex < 0) return;
        SetMode(ModeCombo.SelectedIndex == 1 ? PanelMode.Wall : PanelMode.Sources);
    }

    private void ViewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || ViewCombo.SelectedIndex < 0) return;
        _cardView = ViewCombo.SelectedIndex == 1;
        RefreshSources();
    }

    private async void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || _service is null) return;
        if (LayoutCombo.SelectedItem is not ComboBoxItem { Tag: int edge }) return;
        var layout = WallLayout.FromEdge(edge);
        if (layout is null) return;
        await _service.SetLayoutAsync(layout);
        if (_mode == PanelMode.Wall)
            RebuildWallGrid();
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var dlg = new AddSourceDialog(_service, Window.GetWindow(this));
        if (dlg.ShowDialog() == true)
            RefreshAll();
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var rows = SourcesGrid.SelectedItems.OfType<SourceRowVm>().ToList();
        if (rows.Count == 0) return;
        var label = rows.Count == 1 ? rows[0].Name : rows.Count.ToString();
        var ok = MessageBox.Show(
            string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), label),
            Loc.T("Tool.VideoWall.Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        foreach (var row in rows)
            await _service.RemoveSourceAsync(row.Id);
        RefreshAll();
    }

    private void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            BeginPlayback(id);
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button { Tag: string id }) return;
        var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == id);
        if (src is null) return;
        var dlg = new AddSourceDialog(_service, Window.GetWindow(this), src);
        if (dlg.ShowDialog() == true)
            RefreshAll();
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button { Tag: string id }) return;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        var ok = MessageBox.Show(
            string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), row?.Name ?? id),
            Loc.T("Tool.VideoWall.Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        await _service.RemoveSourceAsync(id);
        RefreshAll();
    }

    private SourceRowVm? SelectedRow()
        => SourcesGrid.SelectedItem as SourceRowVm;

    private void RebuildWallGrid()
    {
        if (_service is null) return;
        WallGrid.Children.Clear();
        var layout = _service.Layout;
        WallGrid.Rows = layout.Rows;
        WallGrid.Columns = layout.Columns;
        var tiles = _service.Tiles;

        for (var i = 0; i < layout.TileCount; i++)
        {
            var tile = i < tiles.Count ? tiles[i] : null;
            var border = new Border
            {
                Margin = new Thickness(3),
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                BorderThickness = new Thickness(1),
                Background = Brushes.Black,
                Cursor = Cursors.Hand,
                Tag = i
            };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            string title;
            string sub;
            if (tile?.SourceId is { } sid)
            {
                var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == sid);
                title = src?.Name ?? sid;
                sub = StatusTextFor(tile.State);
            }
            else
            {
                title = Loc.T("Tool.VideoWall.EmptyTile");
                sub = "";
            }

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4)
            });
            if (!string.IsNullOrEmpty(sub))
                stack.Children.Add(new TextBlock
                {
                    Text = sub,
                    Foreground = Brushes.LightGray,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            border.Child = stack;
            border.MouseLeftButtonDown += Tile_Click;
            WallGrid.Children.Add(border);
        }
    }

    private async void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (_service is null || sender is not Border { Tag: int index }) return;
        if (e.ClickCount >= 2)
        {
            var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
            if (tile?.SourceId is { } id)
                BeginPlayback(id);
            return;
        }

        var menu = new ContextMenu();
        foreach (var s in _service.Catalog.Sources)
        {
            var item = new MenuItem { Header = s.Name, Tag = (index, s.Id) };
            item.Click += async (_, _) =>
            {
                if (item.Tag is ValueTuple<int, string> t)
                    await _service.BindTileAsync(t.Item1, t.Item2);
                RebuildWallGrid();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = Loc.T("Tool.VideoWall.Stop") };
        clear.Click += async (_, _) =>
        {
            await _service.ClearTileAsync(index);
            RebuildWallGrid();
        };
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    private void SourcesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = SelectedRow();
        if (row is not null)
            BeginPlayback(row.Id);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            SourcesGrid.SelectedItem = fe.DataContext;
            if (e.ClickCount >= 2 && fe.DataContext is SourceRowVm row)
            {
                BeginPlayback(row.Id);
                e.Handled = true;
            }
        }
    }

    private void BeginPlayback(string sourceId)
    {
        _pendingPlayId = sourceId;
        SetMode(PanelMode.Playback);
        StatusBar.Text = Loc.T("Tool.VideoWall.StatusConnecting");
        if (PlayerView.IsLoaded)
            _ = StartPlaybackAsync(sourceId);
    }

    private async void PlayerView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingPlayId))
            await StartPlaybackAsync(_pendingPlayId);
    }

    private async Task StartPlaybackAsync(string sourceId)
    {
        if (_service is null) return;
        try
        {
            var hwnd = (PresentationSource.FromVisual(PlayerView) as HwndSource)?.Handle ?? IntPtr.Zero;
            await _service.SinglePlayback.StopAsync();
            await _service.SinglePlayback.PlaySourceAsync(sourceId, hwnd);
            if (hwnd != IntPtr.Zero)
                _service.SinglePlayback.SetRenderTarget(hwnd);
            StatusReporter?.Invoke(Loc.T("Tool.VideoWall.Play"));
        }
        catch (Exception ex)
        {
            PlaybackStats.Text = ex.Message;
        }
    }

    private async void PlaybackBackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        SetMode(PanelMode.Sources);
    }
}
