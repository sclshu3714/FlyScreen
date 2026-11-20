using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.ToolSdk;
using FlyScreen.Ui.Maui;
using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using Grid = Microsoft.Maui.Controls.Grid;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>Maui 视频墙：对齐 Win 结构（源浏览 / 画布 / 单路回放）；Maui-Win 用 Win32 子 HWND 嵌入 LibVLC。</summary>
public sealed class VideoWallPanel : ContentView
{
    private static readonly int[] LayoutEdges = [1, 2, 3, 4];

    private enum PanelMode { Sources, Wall, Playback }
    private enum BrowseView { Card, List, Canvas }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<SourceRowVm> _rows = new();
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _card;
    private readonly Color _blue;
    private readonly Color _warn;
    private readonly Color _listBg;

    private readonly Border _mainToolbar;
    private readonly Border _viewCardBtn;
    private readonly Border _viewListBtn;
    private readonly Border _viewCanvasBtn;
    private readonly Path _viewCardIcon;
    private readonly Path _viewListIcon;
    private readonly Path _viewCanvasIcon;
    private readonly VideoWallDropDown _layoutDrop;
    private readonly HorizontalStackLayout _carouselPanel;
    private readonly CheckBox _carouselToggle;
    private readonly Label _carouselPageText;
    private readonly VideoWallDropDown _groupDrop;
    private readonly Entry _search;
    private readonly Border _deleteBtn;
    private readonly IDispatcherTimer _carouselTimer;

    private readonly Grid _contentHost;
    private readonly Grid _sourcesPane;
    private readonly CollectionView _listView;
    private readonly CheckBox _headerSelectAll;
    private readonly ScrollView _cardsHost;
    private readonly FlexLayout _cardsFlex;
    private readonly VerticalStackLayout _emptyState;
    private readonly Grid _wallHost;
    private readonly Grid _wallGrid;
    private readonly Border _carouselPrevBtn;
    private readonly Border _carouselNextBtn;
    private readonly HorizontalStackLayout _carouselDots;
    private readonly VideoWallPlayerControl _player;
    private readonly Grid _mainPane;

    private readonly AbsoluteLayout _root;
    private readonly VideoWallFlyoutHost _flyout;

    private readonly Dictionary<int, Border> _tileBorders = new();
    private readonly Dictionary<int, Label> _tileTitleBlocks = new();
    private readonly Dictionary<int, Label> _tileStatusBlocks = new();
    private readonly Dictionary<int, Button> _tileRestoreButtons = new();
    private readonly Dictionary<int, LoadingOverlay> _tileLoadingOverlays = new();
#if WINDOWS
    private readonly Dictionary<int, VideoWallHwndSurface> _tileSurfaces = new();
#endif
    private readonly Dictionary<int, VideoWallFrameSurface> _tileFrameSurfaces = new();

    private PanelMode _mode = PanelMode.Sources;
    private BrowseView _browseView = BrowseView.List;
    private string _groupFilter = "";
    private bool _suppressLayout;
    private bool _suppressCarouselToggle;
    private bool _carouselPointerOver;
    private int _carouselHoverDepth;

    public Action<string>? StatusReporter
    {
        get => _statusReporter;
        set
        {
            _statusReporter = value;
            _player.StatusReporter = value;
        }
    }

    private Action<string>? _statusReporter;

    public VideoWallPanel()
    {
        _service = ToolService.Instance.GetToolService<IVideoWallService>();

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        _text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        _muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        _line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        _card = Color.FromArgb(dark ? "#1E293B" : "#FFFFFF");
        _listBg = Color.FromArgb(dark ? "#0F172A" : "#F9FAFB");
        _blue = Color.FromArgb("#2563EB");
        _warn = Color.FromArgb("#DC2626");

        _search = new Entry
        {
            Placeholder = Loc.T("Tool.VideoWall.Search"),
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = _card,
            TextColor = _text
        };
        _search.TextChanged += (_, _) =>
        {
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };

        _carouselTimer = (Application.Current?.Dispatcher ?? throw new InvalidOperationException("No dispatcher"))
            .CreateTimer();
        _carouselTimer.Interval = TimeSpan.FromMilliseconds(WallCarousel.DefaultIntervalMs);
        _carouselTimer.Tick += OnCarouselTimerTick;

        var addPair = VideoWallMauiIcons.CreateIconButton(
            VideoWallMauiIcons.Geo.Add, Colors.White, Loc.T("Tool.VideoWall.Add"),
            VideoWallMauiIcons.ToolbarKind.Primary, _text, _card, _line, _blue, _warn);
        VideoWallMauiIcons.WireClicked(addPair.Host, (_, _) => OpenDialog(edit: null));

        var scanPair = VideoWallMauiIcons.CreateIconButton(
            VideoWallMauiIcons.Geo.Scan, _blue, Loc.T("Tool.VideoWall.Scan"),
            VideoWallMauiIcons.ToolbarKind.Normal, _text, _card, _line, _blue, _warn);
        VideoWallMauiIcons.WireClicked(scanPair.Host, (_, _) => OpenScanDialog());

        var deletePair = VideoWallMauiIcons.CreateIconButton(
            VideoWallMauiIcons.Geo.Delete, _warn, Loc.T("Tool.VideoWall.Delete"),
            VideoWallMauiIcons.ToolbarKind.Danger, _text, _card, _line, _blue, _warn,
            margin: new Thickness(0, 0, 8, 0));
        _deleteBtn = deletePair.Host;
        _deleteBtn.IsEnabled = false;
        VideoWallMauiIcons.WireClicked(_deleteBtn, async (_, _) => await DeleteSelectedAsync());

        var cardPair = VideoWallMauiIcons.CreateSegmentIconButton(
            VideoWallMauiIcons.Geo.ViewCard, Loc.T("Tool.VideoWall.ViewCard"),
            VideoWallMauiIcons.SegmentCorner.Left, _text, _card, _line);
        _viewCardBtn = cardPair.Host;
        _viewCardIcon = cardPair.Icon;
        VideoWallMauiIcons.WireClicked(_viewCardBtn, (_, _) => ApplyBrowseView(BrowseView.Card));

        var listPair = VideoWallMauiIcons.CreateSegmentIconButton(
            VideoWallMauiIcons.Geo.ViewList, Loc.T("Tool.VideoWall.ViewList"),
            VideoWallMauiIcons.SegmentCorner.Middle, _text, _card, _line);
        _viewListBtn = listPair.Host;
        _viewListIcon = listPair.Icon;
        VideoWallMauiIcons.WireClicked(_viewListBtn, (_, _) => ApplyBrowseView(BrowseView.List));

        var canvasPair = VideoWallMauiIcons.CreateSegmentIconButton(
            VideoWallMauiIcons.Geo.ViewCanvas, Loc.T("Tool.VideoWall.ViewCanvas"),
            VideoWallMauiIcons.SegmentCorner.Right, _text, _card, _line);
        _viewCanvasBtn = canvasPair.Host;
        _viewCanvasIcon = canvasPair.Icon;
        VideoWallMauiIcons.WireClicked(_viewCanvasBtn, (_, _) => ApplyBrowseView(BrowseView.Canvas));

        _layoutDrop = new VideoWallDropDown(_text, _muted, _line, _card);
        _layoutDrop.SetOptions(["1×1", "2×2", "3×3", "4×4"]);
        _layoutDrop.SelectedIndex = 1; // 默认 2×2
        _layoutDrop.IsVisible = false;
        _layoutDrop.SelectionChanged += async _ => await OnLayoutChangedAsync();

        _carouselToggle = new CheckBox
        {
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        var carouselLabel = new Label
        {
            Text = Loc.T("Tool.VideoWall.Carousel"),
            TextColor = _text,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        carouselLabel.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => _carouselToggle.IsChecked = !_carouselToggle.IsChecked)
        });
        ToolTipProperties.SetText(_carouselToggle, Loc.T("Tool.VideoWall.CarouselHint"));
        ToolTipProperties.SetText(carouselLabel, Loc.T("Tool.VideoWall.CarouselHint"));
        _carouselToggle.CheckedChanged += async (_, _) => await OnCarouselToggleChangedAsync();

        _carouselPageText = new Label
        {
            TextColor = _muted,
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center
        };

        _carouselPanel = new HorizontalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            Margin = new Thickness(0, 0, 8, 0),
            Children = { _carouselToggle, carouselLabel, _carouselPageText }
        };

        _groupDrop = new VideoWallDropDown(_text, _muted, _line, _card);
        _groupDrop.SelectedIndex = 0;
        _groupDrop.SelectionChanged += __ =>
        {
            var v = _groupDrop.Value;
            _groupFilter = string.Equals(v, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) ? "" : v;
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };

        var viewToggle = new HorizontalStackLayout
        {
            Spacing = 0,
            Children = { _viewCardBtn, _viewListBtn, _viewCanvasBtn }
        };
        // 手机窄屏：工具条换行；桌面仍单行
        View toolbarContent;
        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            toolbarContent = new FlexLayout
            {
                Direction = FlexDirection.Row,
                Wrap = FlexWrap.Wrap,
                JustifyContent = FlexJustify.Start,
                AlignItems = FlexAlignItems.Center,
                AlignContent = FlexAlignContent.Start,
                Children =
                {
                    addPair.Host,
                    scanPair.Host,
                    _deleteBtn,
                    viewToggle,
                    _layoutDrop,
                    _carouselPanel,
                    _groupDrop,
                    _search
                }
            };
            _search.MinimumWidthRequest = 140;
            _search.WidthRequest = 180;
        }
        else
        {
            toolbarContent = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                ],
                ColumnSpacing = 6,
                Children =
                {
                    addPair.Host,
                    scanPair.Host.Also(v => Grid.SetColumn(v, 1)),
                    _deleteBtn.Also(v => Grid.SetColumn(v, 2)),
                    viewToggle.Also(v => Grid.SetColumn(v, 3)),
                    _layoutDrop.Also(v => Grid.SetColumn(v, 4)),
                    _carouselPanel.Also(v => Grid.SetColumn(v, 5)),
                    _groupDrop.Also(v => Grid.SetColumn(v, 6)),
                    _search.Also(v => Grid.SetColumn(v, 7))
                }
            };
        }

        _mainToolbar = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = toolbarContent
        };

        _headerSelectAll = new CheckBox
        {
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        _headerSelectAll.CheckedChanged += HeaderSelectAll_CheckedChanged;
        // 双击表头复选框：反选
        var invertTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        invertTap.Tapped += (_, _) =>
        {
            if (_suppressHeaderSelect) return;
            _suppressHeaderSelect = true;
            try
            {
                foreach (var row in _rows)
                    row.IsSelected = !row.IsSelected;
                UpdateDeleteEnabled();
            }
            finally
            {
                _suppressHeaderSelect = false;
                SyncHeaderSelectAll();
            }
        };
        _headerSelectAll.GestureRecognizers.Add(invertTap);

        _listView = new CollectionView
        {
            ItemsSource = _rows,
            // 关闭平台自带多选勾选（会挤进名称列）；改用独立复选列
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(BuildListRow),
            Header = BuildListHeader(),
            BackgroundColor = _listBg
        };

        // FlexLayout 定宽换行：避免 GridItemsLayout 均分列宽导致间距过大/右边裁切圆角
        _cardsFlex = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            JustifyContent = FlexJustify.Start,
            AlignItems = FlexAlignItems.Start,
            AlignContent = FlexAlignContent.Start
        };
        BindableLayout.SetItemsSource(_cardsFlex, _rows);
        BindableLayout.SetItemTemplate(_cardsFlex, new DataTemplate(BuildCard));
        _cardsHost = new ScrollView
        {
            Orientation = ScrollOrientation.Vertical,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Padding = new Thickness(4),
            IsVisible = false,
            BackgroundColor = _listBg,
            Content = _cardsFlex
        };
        _cardsHost.SizeChanged += (_, _) =>
        {
            if (_cardsHost.Width <= 1) return;
            var next = ComputeCardWidth(_cardsHost.Width);
            if (Math.Abs(next - _cardWidth) < 0.5) return;
            _cardWidth = next;
            ApplyCardWidths();
        };

        _emptyState = new VerticalStackLayout
        {
            IsVisible = false,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 8,
                Children =
                {
                new Label
                {
                    Text = Loc.T("Tool.VideoWall.EmptyTitle"),
                    FontSize = 15,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = _text,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                new Label
                {
                    Text = Loc.T("Tool.VideoWall.EmptyHint"),
                    FontSize = 13,
                    TextColor = _muted,
                    HorizontalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.WordWrap,
                    MaximumWidthRequest = 360
                }
            }
        };

        var sourcesBody = new Grid
        {
            Children =
            {
                _listView,
                _cardsHost,
                _emptyState
            }
        };

        _sourcesPane = new Grid
        {
            Children =
            {
                new Border
                {
                    Stroke = _line,
                    BackgroundColor = _listBg,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    Content = sourcesBody
                }
            }
        };

        _wallGrid = new Grid
        {
            RowSpacing = 4,
            ColumnSpacing = 4,
            BackgroundColor = Colors.Black
        };

        _carouselPrevBtn = new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Padding = 0,
            BackgroundColor = Color.FromArgb("#99000000"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            IsVisible = false,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Content = VideoWallMauiIcons.CreatePath(IconCodes.ChevronLeft, Colors.White, 16)
        };
        _carouselNextBtn = new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Padding = 0,
            BackgroundColor = Color.FromArgb("#99000000"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            IsVisible = false,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Content = VideoWallMauiIcons.CreatePath(IconCodes.ChevronRight, Colors.White, 16)
        };
        _carouselDots = new HorizontalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 14),
            IsVisible = false
        };
        VideoWallMauiIcons.WireClicked(_carouselPrevBtn, async () => await StepCarouselAsync(next: false));
        VideoWallMauiIcons.WireClicked(_carouselNextBtn, async () => await StepCarouselAsync(next: true));
        ToolTipProperties.SetText(_carouselPrevBtn, Loc.T("Tool.VideoWall.CarouselPrev"));
        ToolTipProperties.SetText(_carouselNextBtn, Loc.T("Tool.VideoWall.CarouselNext"));
        WireCarouselHoverTarget(_carouselPrevBtn);
        WireCarouselHoverTarget(_carouselNextBtn);
        WireCarouselHoverTarget(_carouselDots);

        _wallHost = new Grid
        {
            IsVisible = false,
            BackgroundColor = Colors.Transparent,
            Children = { _wallGrid, _carouselPrevBtn, _carouselNextBtn, _carouselDots }
        };
        WireCarouselHoverTarget(_wallHost);

        _player = new VideoWallPlayerControl { IsVisible = false };
        _player.Closed += () => _ = OnPlayerClosedAsync();
        _player.RequestInAppMaximize += () => _ = OnPlayerRequestInAppMaximizeAsync();

        _contentHost = new Grid
        {
            Children = { _sourcesPane, _wallHost, _player }
        };

        _mainPane = new Grid
        {
            Padding = 8,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)],
            Children =
            {
                _mainToolbar,
                _contentHost.Also(v => Grid.SetRow(v, 1))
            }
        };
        var main = _mainPane;

        // 下拉浮层挂在面板根；添加源/扫描改走主窗 PushModal，避免被工具区挤压
        var flyoutLayer = new AbsoluteLayout { IsVisible = false, ZIndex = 400 };
        _flyout = new VideoWallFlyoutHost(flyoutLayer);

        _layoutDrop.BindFlyout(_flyout);
        _groupDrop.BindFlyout(_flyout);

        _root = new AbsoluteLayout();
        AbsoluteLayout.SetLayoutBounds(main, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(main, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        AbsoluteLayout.SetLayoutBounds(flyoutLayer, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(flyoutLayer, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);

        _root.Children.Add(main);
        _root.Children.Add(flyoutLayer);

        Content = _root;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (_service is null)
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));
        else if (!_service.Backend.IsAvailable)
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));

        SyncBrowseChrome();
        RefreshSources();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged += OnCatalogChanged;
        _service.SessionChanged += OnSessionChanged;
        _player.AttachService(_service);
        PopulateGroups();
        SyncLayoutFromService();
        SyncBrowseChrome();
        RefreshAll();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _carouselTimer.Stop();
        _player.AttachService(null);
        _ = _player.StopAsync();
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        if (_service is null) return;
        _service.CatalogChanged -= OnCatalogChanged;
        _service.SessionChanged -= OnSessionChanged;
    }

    private void OnCatalogChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshSources();
            PopulateGroups();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        });

    private void OnSessionChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_mode == PanelMode.Wall)
                RefreshWallOverlays();
        });

    private void ReportLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusReporter?.Invoke(message);
    }

    private void PopulateGroups()
    {
        if (_service is null) return;
        var options = new List<string> { Loc.T("Tool.VideoWall.GroupAll") };
        options.AddRange(_service.Catalog.Groups.Select(g => g.Name));
        _groupDrop.SetOptions(options.ToArray());
        if (_groupDrop.SelectedIndex < 0)
            _groupDrop.SelectedIndex = 0;
    }

    private void SyncLayoutFromService()
    {
        if (_service is null) return;
        _suppressLayout = true;
        try
        {
            var edge = _service.Layout.Rows;
            var idx = Array.IndexOf(LayoutEdges, edge);
            _layoutDrop.SelectedIndex = idx >= 0 ? idx : 1;
        }
        finally { _suppressLayout = false; }
    }

    private void RefreshAll()
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            _ = RefreshCanvasAsync();
        SyncBrowseChrome();
    }

    private void RefreshSources()
    {
        if (_service is null) return;
        var q = (_search.Text ?? "").Trim();
        _rows.Clear();
        foreach (var s in _service.Catalog.Sources)
        {
            if (!string.IsNullOrEmpty(_groupFilter) && !MatchesGroup(s, _groupFilter))
                continue;
            if (q.Length > 0 &&
                !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !s.Endpoint.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            _rows.Add(ToRow(s));
        }

        var empty = _rows.Count == 0;
        _emptyState.IsVisible = empty && _mode == PanelMode.Sources;
        _listView.IsVisible = _browseView == BrowseView.List && !empty;
        _cardsHost.IsVisible = _browseView == BrowseView.Card && !empty;
        if (_cardsHost.IsVisible)
            Dispatcher.Dispatch(() =>
            {
                if (_cardsHost.Width <= 1) return;
                var next = ComputeCardWidth(_cardsHost.Width);
                if (Math.Abs(next - _cardWidth) >= 0.5)
                    _cardWidth = next;
                ApplyCardWidths();
            });
        SyncHeaderSelectAll();
        UpdateDeleteEnabled();
    }

    private static bool MatchesGroup(VideoSourceDescriptor s, string groupName)
        => string.Equals(s.GroupId, groupName, StringComparison.OrdinalIgnoreCase);

    private SourceRowVm ToRow(VideoSourceDescriptor s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        KindText = KindLabel(s.Kind),
        AddressRedacted = VideoEndpointParser.RedactAddress(s.Endpoint.Address),
        StatusText = ValidationLabel(s.ValidationState)
    };

    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        _mainToolbar.IsVisible = mode != PanelMode.Playback;
        _sourcesPane.IsVisible = mode == PanelMode.Sources;
        _wallHost.IsVisible = mode == PanelMode.Wall;
        _player.IsVisible = mode == PanelMode.Playback;
        if (mode != PanelMode.Wall)
        {
            _carouselPointerOver = false;
            _carouselHoverDepth = 0;
            UpdateCarouselOverlayVisibility();
        }
        if (mode != PanelMode.Playback)
            _ = _player.StopAsync();
        _mainPane.Padding = mode == PanelMode.Playback ? new Thickness(0) : new Thickness(8);
        if (mode == PanelMode.Wall)
            RebuildWallGrid();
        else if (mode == PanelMode.Sources)
            RefreshSources();

        SyncBrowseChrome();
    }

    private async void ApplyBrowseView(BrowseView view)
    {
        var leavingCanvas = _browseView == BrowseView.Canvas && view != BrowseView.Canvas;
        _browseView = view;
        if (leavingCanvas)
        {
            _carouselTimer.Stop();
            await RecycleWallPlaybackAsync();
        }

        if (view == BrowseView.Canvas)
        {
            SetMode(PanelMode.Wall);
            await RefreshCanvasAsync();
        }
        else
            SetMode(PanelMode.Sources);
    }

    /// <summary>单路返回：画布模式回到墙并按轮播页重建；否则回到列表/卡片。</summary>
    private async Task OnPlayerClosedAsync()
    {
        if (_browseView == BrowseView.Canvas)
        {
            SetMode(PanelMode.Wall);
            await RefreshCanvasAsync();
        }
        else
        {
            SetMode(PanelMode.Sources);
        }
    }

    private void SyncBrowseChrome()
    {
        var browsing = _mode is PanelMode.Sources or PanelMode.Wall;
        _viewCardBtn.IsEnabled = browsing;
        _viewListBtn.IsEnabled = browsing;
        _viewCanvasBtn.IsEnabled = browsing;
        VideoWallMauiIcons.SetSegmentActive(_viewCardBtn, _viewCardIcon, browsing && _browseView == BrowseView.Card, _blue, _muted, _card, _line);
        VideoWallMauiIcons.SetSegmentActive(_viewListBtn, _viewListIcon, browsing && _browseView == BrowseView.List, _blue, _muted, _card, _line);
        VideoWallMauiIcons.SetSegmentActive(_viewCanvasBtn, _viewCanvasIcon, browsing && _browseView == BrowseView.Canvas, _blue, _muted, _card, _line);
        var canvasChrome = browsing && _browseView == BrowseView.Canvas;
        _layoutDrop.IsVisible = canvasChrome;
        _carouselPanel.IsVisible = canvasChrome;
        _suppressCarouselToggle = true;
        try { _carouselToggle.IsChecked = _service?.CarouselEnabled == true; }
        finally { _suppressCarouselToggle = false; }
        UpdateCarouselChrome();
        _layoutDrop.BindFlyout(_flyout);
        _groupDrop.BindFlyout(_flyout);
    }

    private async Task OnLayoutChangedAsync()
    {
        if (_suppressLayout || _service is null || _browseView != BrowseView.Canvas) return;
        var idx = _layoutDrop.SelectedIndex;
        if (idx < 0 || idx >= LayoutEdges.Length) return;
        var layout = WallLayout.FromEdge(LayoutEdges[idx]);
        if (layout is null) return;
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        await _service.SetLayoutAsync(layout);
        if (_mode != PanelMode.Wall)
            SetMode(PanelMode.Wall);
        else
            RebuildWallGrid();
        await RefreshCanvasAsync();
    }

    private async Task RecycleWallPlaybackAsync()
    {
        _carouselTimer.Stop();
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        if (_service is not null)
        {
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
        }

        _wallGrid.Children.Clear();
        _wallGrid.RowDefinitions.Clear();
        _wallGrid.ColumnDefinitions.Clear();
        _tileBorders.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileRestoreButtons.Clear();
        _tileLoadingOverlays.Clear();
#if WINDOWS
        _tileSurfaces.Clear();
#endif
        foreach (var s in _tileFrameSurfaces.Values)
            s.Clear();
        _tileFrameSurfaces.Clear();
    }

    private async void OnCarouselTimerTick(object? sender, EventArgs e)
    {
        if (_service is null || _mode != PanelMode.Wall || !_service.CarouselEnabled) return;
        await _service.AdvanceCarouselAsync();
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
    }

    private async Task OnCarouselToggleChangedAsync()
    {
        if (_suppressCarouselToggle || _service is null || _mode != PanelMode.Wall) return;
        await _service.SetCarouselEnabledAsync(_carouselToggle.IsChecked == true);
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    /// <summary>按当前分组/搜索同步源列表并铺当前轮播页。</summary>
    private async Task RefreshCanvasAsync()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        _service.SetCarouselSources(_rows.Select(r => r.Id).ToList());
        await _service.ApplyCarouselPageAsync(autoStart: false);
        RebuildWallGrid();
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    private async Task PresentCarouselPageAsync()
    {
        if (_service is null) return;
        for (var i = 0; i < _service.Layout.TileCount; i++)
        {
            PopulateTileCell(i, preserveOtherPlayers: true);
            var tile = i < _service.Tiles.Count ? _service.Tiles[i] : null;
            if (tile?.SourceId is null || !tile.HasPlayer) continue;
#if WINDOWS
            if (OperatingSystem.IsWindows() && _tileSurfaces.TryGetValue(i, out var surface))
            {
                var hwnd = await WaitForSurfaceHwndAsync(surface).ConfigureAwait(true);
                if (hwnd == IntPtr.Zero)
                {
                    ReportLog($"视频墙格{i}: HWND 未就绪，跳过开播以免弹出独立窗口");
                    continue;
                }

                tile.SetRenderTarget(hwnd);
            }
            else
#endif
            if (_tileFrameSurfaces.TryGetValue(i, out var frame))
            {
                tile.AttachFrameSink(frame);
            }

            try { await _service.StartTileAsync(i); }
            catch (Exception ex) { ReportLog($"视频墙格{i}开播失败: {ex.Message}"); }
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
            _carouselPageText.Text = "";
            _carouselDots.Children.Clear();
            UpdateCarouselOverlayVisibility();
            return;
        }

        _carouselPageText.Text = string.Format(
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
        _carouselPrevBtn.IsVisible = showArrows;
        _carouselNextBtn.IsVisible = showArrows;
        _carouselDots.IsVisible = nav;
    }

    private void RebuildCarouselDots()
    {
        _carouselDots.Children.Clear();
        if (!CarouselNavAvailable || _service is null) return;

        var page = _service.CarouselPage;
        var count = _service.CarouselPageCount;
        // 附件样式：短胶囊 + 当前页加长；色跟主题
        var active = _blue;
        var idle = _line;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var on = i == page;
            var pill = new Border
            {
                WidthRequest = on ? 28 : 16,
                HeightRequest = 6,
                MinimumWidthRequest = on ? 28 : 16,
                MaximumWidthRequest = on ? 28 : 16,
                HorizontalOptions = LayoutOptions.Start,
                BackgroundColor = on ? active : idle,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 3 }
            };
            ToolTipProperties.SetText(pill, string.Format(Loc.T("Tool.VideoWall.CarouselGoto"), i + 1));
            VideoWallMauiIcons.WireClicked(pill, async () => await GoToCarouselPageUiAsync(index));
            _carouselDots.Children.Add(pill);
        }
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

    private void WireCarouselHoverTarget(View view)
    {
        var ptr = new PointerGestureRecognizer();
        ptr.PointerEntered += (_, _) => OnCarouselHoverEnter();
        ptr.PointerExited += (_, _) => OnCarouselHoverExit();
        view.GestureRecognizers.Add(ptr);
    }

    private void OnCarouselHoverEnter()
    {
        _carouselHoverDepth++;
        if (_carouselPointerOver) return;
        _carouselPointerOver = true;
        UpdateCarouselOverlayVisibility();
        SyncCarouselTimer();
    }

    private void OnCarouselHoverExit()
    {
        _carouselHoverDepth = Math.Max(0, _carouselHoverDepth - 1);
        if (_carouselHoverDepth > 0) return;
        // 格与格之间移动时 Exit/Enter 会交错，短暂延迟再判定是否真正离开
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _carouselPointerOver = false;
            UpdateCarouselOverlayVisibility();
            SyncCarouselTimer();
            return;
        }

        dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(60), () =>
        {
            if (_carouselHoverDepth > 0) return;
            _carouselPointerOver = false;
            UpdateCarouselOverlayVisibility();
            SyncCarouselTimer();
        });
    }

    private void RebuildWallGrid()
    {
        if (_service is null) return;
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        _wallGrid.Children.Clear();
        _wallGrid.RowDefinitions.Clear();
        _wallGrid.ColumnDefinitions.Clear();
        _tileBorders.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileRestoreButtons.Clear();
        _tileLoadingOverlays.Clear();
#if WINDOWS
        _tileSurfaces.Clear();
#endif

        var layout = _service.Layout;
        for (var r = 0; r < layout.Rows; r++)
            _wallGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (var c = 0; c < layout.Columns; c++)
            _wallGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (var i = 0; i < layout.TileCount; i++)
        {
            var border = CreateTileBorderShell(i);
            _tileBorders[i] = border;
            WireCarouselHoverTarget(border);
            Grid.SetRow(border, i / layout.Columns);
            Grid.SetColumn(border, i % layout.Columns);
            _wallGrid.Children.Add(border);
            PopulateTileCell(i, preserveOtherPlayers: false);
        }

        ApplyWallFocus();
    }

    /// <summary>应用内最大化：聚焦格跨满整个 Grid，其余格隐藏（播放器保留，由服务层暂停）。</summary>
    private void ApplyWallFocus()
    {
        if (_service is null) return;
        var layout = _service.Layout;
        var focus = _service.FocusedTileIndex;
        var focused = focus is int f && _tileBorders.ContainsKey(f);

        foreach (var (index, border) in _tileBorders)
        {
            if (focused)
            {
                var isFocus = index == focus;
                border.IsVisible = isFocus;
                Grid.SetRow(border, 0);
                Grid.SetColumn(border, 0);
                Grid.SetRowSpan(border, isFocus ? layout.Rows : 1);
                Grid.SetColumnSpan(border, isFocus ? layout.Columns : 1);
            }
            else
            {
                border.IsVisible = true;
                Grid.SetRow(border, index / layout.Columns);
                Grid.SetColumn(border, index % layout.Columns);
                Grid.SetRowSpan(border, 1);
                Grid.SetColumnSpan(border, 1);
            }
        }

        foreach (var (index, button) in _tileRestoreButtons)
            button.IsVisible = focused && index == focus;
    }

    private async Task OnPlayerRequestInAppMaximizeAsync()
    {
        if (_service is null) return;
        var sourceId = _service.SinglePlayback.CurrentSourceId;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        try
        {
#if WINDOWS
            // 单画面播放让位给墙格（同一源不要双播）
            await _player.StopAsync();
#endif
            _browseView = BrowseView.Canvas;
            SetMode(PanelMode.Wall);

            var tiles = _service.Tiles.ToList();
            var index = tiles.FindIndex(t => t.SourceId == sourceId);
            if (index < 0)
            {
                index = tiles.FindIndex(t => t.SourceId is null);
                if (index < 0) index = 0;
#if WINDOWS
                await BindTileInPlaceAsync(index, sourceId);
#else
                // 无墙内视频面的端：只占位，单画面播放保持不中断
                await _service.BindTileAsync(index, sourceId, autoStart: false);
                PopulateTileCell(index, preserveOtherPlayers: true);
#endif
            }
            else
            {
                index = tiles[index].Index;
            }

            await _service.FocusTileAsync(index);
            ApplyWallFocus();
        }
        catch (Exception ex)
        {
            ReportLog($"视频墙最大化失败: {ex.Message}");
        }
    }

    private Border CreateTileBorderShell(int index)
    {
        var border = new Border
        {
            Margin = 3,
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = Colors.Black,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            ClassId = index.ToString()
        };
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, _) => OnTileDoubleTapped(index);
        border.GestureRecognizers.Add(doubleTap);

        return border;
    }

    private void PopulateTileCell(int index, bool preserveOtherPlayers)
    {
        if (_service is null) return;
        if (!_tileBorders.TryGetValue(index, out var border))
        {
            if (!preserveOtherPlayers)
                RebuildWallGrid();
            return;
        }

#if WINDOWS
        if (_tileSurfaces.TryGetValue(index, out _))
        {
            if (index < _service.Tiles.Count)
                _service.Tiles[index].SetRenderTarget(IntPtr.Zero);
            _tileSurfaces.Remove(index);
        }
#endif

        _tileTitleBlocks.Remove(index);
        _tileStatusBlocks.Remove(index);
        _tileRestoreButtons.Remove(index);
        _tileLoadingOverlays.Remove(index);

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        var root = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            ]
        };

        var title = new Label
        {
            Text = ResolveTileTitle(tile),
            TextColor = Colors.White,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation,
            Margin = new Thickness(6, 4, 6, 0)
        };
        var status = new Label
        {
            Text = tile?.SourceId is null ? "" : StatusTextFor(tile.State),
            TextColor = Colors.LightGray,
            FontSize = 11,
            Margin = new Thickness(6, 0, 6, 4)
        };

        // 最大化后的「还原」入口（仅聚焦格可见）
        var restoreBtn = new Button
        {
            Text = Loc.T("Tool.VideoWall.MaximizeExit"),
            FontSize = 11,
            Padding = new Thickness(8, 0),
            BackgroundColor = Color.FromArgb("#CC374151"),
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            IsVisible = _service.FocusedTileIndex == index
        };
        restoreBtn.Clicked += async (_, _) =>
        {
            await _service.FocusTileAsync(null);
            ApplyWallFocus();
        };
        _tileRestoreButtons[index] = restoreBtn;

        var chrome = new Grid
        {
            BackgroundColor = Color.FromArgb("#AA111827"),
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            Children =
            {
                new VerticalStackLayout { Children = { title, status } },
                restoreBtn.Also(v => Grid.SetColumn(v, 1))
            }
        };
        Grid.SetRow(chrome, 0);
        root.Children.Add(chrome);
        _tileTitleBlocks[index] = title;
        _tileStatusBlocks[index] = status;

        var body = new Grid();
        if (tile?.SourceId is null)
        {
            body.Children.Add(new Label
            {
                Text = Loc.T("Tool.VideoWall.EmptySlot"),
                TextColor = Colors.Gray,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(8),
                InputTransparent = true
            });
        }
        else if (OperatingSystem.IsWindows())
        {
#if WINDOWS
            var surface = new VideoWallHwndSurface
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            body.Children.Add(surface);
            _tileSurfaces[index] = surface;
#else
            var frame = new VideoWallFrameSurface
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            body.Children.Add(frame);
            _tileFrameSurfaces[index] = frame;
#endif
        }
        else
        {
            var frame = new VideoWallFrameSurface
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            body.Children.Add(frame);
            _tileFrameSurfaces[index] = frame;
        }

        if (tile?.SourceId is not null)
        {
            var loading = new LoadingOverlay { SpinnerSize = 28 };
            if (TilePlayStateUi.ShowsLoading(tile.State))
                loading.Show();
            else
                loading.Hide();
            body.Children.Add(loading);
            _tileLoadingOverlays[index] = loading;
        }

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        border.Content = root;
    }

    private string ResolveTileTitle(TileSession? tile)
    {
        if (tile?.SourceId is not { } sid || _service is null)
            return Loc.T("Tool.VideoWall.EmptySlot");
        return _service.Catalog.Sources.FirstOrDefault(s => s.Id == sid)?.Name ?? sid;
    }

    private void RefreshWallOverlays()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        ApplyWallFocus();
        var tiles = _service.Tiles;
        for (var i = 0; i < _service.Layout.TileCount; i++)
        {
            var tile = i < tiles.Count ? tiles[i] : null;
            var hasSource = tile?.SourceId is not null;
#if WINDOWS
            var hasSurface = _tileSurfaces.ContainsKey(i) || _tileFrameSurfaces.ContainsKey(i);
#else
            var hasSurface = _tileFrameSurfaces.ContainsKey(i);
#endif
            if (hasSource != hasSurface)
            {
                PopulateTileCell(i, preserveOtherPlayers: true);
#if WINDOWS
                if (hasSource && OperatingSystem.IsWindows() && _tileSurfaces.TryGetValue(i, out var surface))
                    _ = RebindTileSurfaceAsync(i, surface);
                else
#endif
                if (hasSource && _tileFrameSurfaces.TryGetValue(i, out var frame)
                    && _service.Tiles.Count > i)
                {
                    _service.Tiles[i].AttachFrameSink(frame);
                    _ = _service.StartTileAsync(i);
                }
                continue;
            }
            if (_tileTitleBlocks.TryGetValue(i, out var title))
                title.Text = ResolveTileTitle(tile);
            if (_tileStatusBlocks.TryGetValue(i, out var status))
                status.Text = hasSource ? StatusTextFor(tile!.State) : "";
            if (_tileLoadingOverlays.TryGetValue(i, out var loading))
            {
                var show = hasSource && tile is not null && TilePlayStateUi.ShowsLoading(tile.State);
                if (show) loading.Show();
                else loading.Hide();
            }
        }
    }

#if WINDOWS
    private async Task<IntPtr> WaitForSurfaceHwndAsync(VideoWallHwndSurface surface, int timeoutMs = 1000)
    {
        if (surface.Hwnd != IntPtr.Zero)
            return surface.Hwnd;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHwndReady() => ready.TrySetResult();
        surface.HwndReady += OnHwndReady;
        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (surface.Hwnd == IntPtr.Zero && Environment.TickCount64 < deadline)
            {
                var waitMs = (int)Math.Min(50, deadline - Environment.TickCount64);
                if (waitMs <= 0)
                    break;
                var completed = await Task.WhenAny(ready.Task, Task.Delay(waitMs)).ConfigureAwait(true);
                if (completed == ready.Task || surface.Hwnd != IntPtr.Zero)
                    break;
            }

            return surface.Hwnd;
        }
        finally
        {
            surface.HwndReady -= OnHwndReady;
        }
    }

    private void DetachAllTileSurfaces()
    {
        if (_service is null) return;
        foreach (var (index, _) in _tileSurfaces)
        {
            if (index < _service.Tiles.Count)
                _service.Tiles[index].SetRenderTarget(IntPtr.Zero);
        }
    }

    private async Task RebindTileSurfaceAsync(int index, VideoWallHwndSurface surface)
    {
        if (_service is null) return;
        var hwnd = await WaitForSurfaceHwndAsync(surface).ConfigureAwait(true);
        if (hwnd == IntPtr.Zero)
        {
            ReportLog($"视频墙格{index}: HWND 未就绪，跳过绑定以免弹出独立窗口");
            return;
        }

        if (index >= _service.Tiles.Count)
            return;
        _service.Tiles[index].SetRenderTarget(hwnd);
    }
#endif

    private async Task BindTileInPlaceAsync(int index, string sourceId)
    {
        if (_service is null) return;
        await _service.BindTileAsync(index, sourceId, autoStart: false);
        PopulateTileCell(index, preserveOtherPlayers: true);
#if WINDOWS
        if (!_tileSurfaces.TryGetValue(index, out var surface))
            return;
        var hwnd = await WaitForSurfaceHwndAsync(surface).ConfigureAwait(true);
        if (hwnd == IntPtr.Zero)
        {
            ReportLog($"视频墙格{index}: HWND 未就绪，跳过开播以免弹出独立窗口");
            return;
        }

        if (index < _service.Tiles.Count)
            _service.Tiles[index].SetRenderTarget(hwnd);
#endif
        await _service.StartTileAsync(index);
        if (_tileTitleBlocks.TryGetValue(index, out var title))
            title.Text = ResolveTileTitle(index < _service.Tiles.Count ? _service.Tiles[index] : null);
        if (_tileStatusBlocks.TryGetValue(index, out var status))
        {
            var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
            status.Text = tile?.SourceId is null ? "" : StatusTextFor(tile.State);
        }
    }

    private void OnTileDoubleTapped(int index)
    {
        if (_service is null) return;
        // 有源双击进单路；画布由过滤源列表自动铺格，不再双击添加
        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.SourceId is { } id)
            BeginPlayback(id);
    }

    private void BeginPlayback(string sourceId)
    {
        _ = BeginPlaybackCoreAsync(sourceId);
    }

    private async Task BeginPlaybackCoreAsync(string sourceId)
    {
        // 画布进单路：停墙与轮播，避免同路双开；返回时按轮播页重建
        if (_browseView == BrowseView.Canvas && _mode == PanelMode.Wall)
            await RecycleWallPlaybackAsync();

        SetMode(PanelMode.Playback);
        // 软件帧出画不依赖 HWND 布局；仍让出一帧以便播放页可见
        await Task.Yield();
        await _player.PlayAsync(sourceId, ResolvePlaybackTitle(sourceId)).ConfigureAwait(true);
    }

    private string ResolvePlaybackTitle(string sourceId)
    {
        var row = _rows.FirstOrDefault(r => r.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(row?.Name)) return row.Name;
        var src = _service?.Catalog.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(src?.Name)) return src.Name;
        return sourceId;
    }

    private async Task DeleteSelectedAsync()
    {
        if (_service is null) return;
        var rows = _rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0) return;

        var page = GetPage();
        if (page is not null)
        {
            var label = rows.Count == 1 ? rows[0].Name : rows.Count.ToString();
            var ok = await page.DisplayAlertAsync(
                Loc.T("Tool.VideoWall.Delete"),
                string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), label),
                Loc.T("Tool.VideoWall.Delete"),
                Loc.T("Tool.VideoWall.Cancel"));
            if (!ok) return;
        }

        foreach (var row in rows)
        {
            if (string.Equals(_service.SinglePlayback.CurrentSourceId, row.Id, StringComparison.Ordinal))
                await _player.StopAsync();
            await _service.RemoveSourceAsync(row.Id);
        }

        RefreshAll();
    }

    private void OpenScanDialog()
    {
        if (_service is null)
        {
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));
            return;
        }

        _ = VideoWallScanPage.ShowAsync(_service, onSourcesChanged: () =>
        {
            MainThread.BeginInvokeOnMainThread(RefreshAll);
        });
    }

    private void OpenDialog(string? edit)
    {
        if (_service is null)
        {
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));
            return;
        }

        _flyout.Close();
        _ = VideoWallSourceDialogPage.ShowAsync(_service, edit, onSaved: () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshAll();
                ReportLog(edit is null
                    ? Loc.T("Tool.VideoWall.AddSource") + " OK"
                    : Loc.T("Tool.VideoWall.Editing") + " OK");
            });
        });
    }

    private bool _suppressHeaderSelect;

    private View BuildListHeader()
    {
        ToolTipProperties.SetText(_headerSelectAll, Loc.T("Tool.VideoWall.SelectAllTip"));
        var hdr = new Grid
        {
            Padding = new Thickness(12, 10),
            BackgroundColor = Color.FromArgb(Application.Current?.RequestedTheme == AppTheme.Dark ? "#0F172A" : "#F3F4F6"),
            ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(36)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(88)),
                new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(72)),
                new ColumnDefinition(new GridLength(108))
            ],
            ColumnSpacing = 8,
            Children =
            {
                _headerSelectAll,
                Header(Loc.T("Tool.VideoWall.ColName")).Also(v => Grid.SetColumn(v, 1)),
                Header(Loc.T("Tool.VideoWall.ColKind")).Also(v => Grid.SetColumn(v, 2)),
                Header(Loc.T("Tool.VideoWall.Field.Address")).Also(v => Grid.SetColumn(v, 3)),
                Header(Loc.T("Tool.VideoWall.ColStatus")).Also(v => Grid.SetColumn(v, 4)),
                Header(Loc.T("Tool.VideoWall.ColActions")).Also(v => Grid.SetColumn(v, 5))
            }
        };
        return hdr;

        Label Header(string t) => new()
        {
            Text = t,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = _muted,
            VerticalOptions = LayoutOptions.Center
        };
    }

    private void HeaderSelectAll_CheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_suppressHeaderSelect) return;

        // 整段 suppress：避免行勾选绑定回写再次触发 CheckedChanged 造成 UI 线程死循环卡死
        _suppressHeaderSelect = true;
        try
        {
            var target = e.Value;
            foreach (var row in _rows)
                row.IsSelected = target;
            UpdateDeleteEnabled();
        }
        finally
        {
            _suppressHeaderSelect = false;
            SyncHeaderSelectAll();
        }
    }

    private void SyncHeaderSelectAll()
    {
        if (_suppressHeaderSelect) return;
        _suppressHeaderSelect = true;
        try
        {
            if (_rows.Count == 0)
            {
                _headerSelectAll.IsChecked = false;
                return;
            }

            var n = _rows.Count(r => r.IsSelected);
            // Maui CheckBox 无 indeterminate，半选时保持 false，避免误显示全选
            _headerSelectAll.IsChecked = n == _rows.Count;
        }
        finally
        {
            _suppressHeaderSelect = false;
        }
    }

    private void UpdateDeleteEnabled()
        => _deleteBtn.IsEnabled = _rows.Any(r => r.IsSelected);

    private View BuildListRow()
    {
        static void CenterLabel(Label l)
        {
            l.VerticalOptions = LayoutOptions.Center;
            l.VerticalTextAlignment = TextAlignment.Center;
        }

        var check = new CheckBox
        {
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        check.SetBinding(CheckBox.IsCheckedProperty, nameof(SourceRowVm.IsSelected));
        check.CheckedChanged += (_, _) =>
        {
            if (_suppressHeaderSelect) return;
            SyncHeaderSelectAll();
            UpdateDeleteEnabled();
        };

        var name = new Label { FontSize = 13, TextColor = _text, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(SourceRowVm.Name));
        CenterLabel(name);
        var kind = new Label { FontSize = 12, TextColor = _muted };
        kind.SetBinding(Label.TextProperty, nameof(SourceRowVm.KindText));
        CenterLabel(kind);
        var addr = new Label { FontSize = 12, TextColor = _muted, LineBreakMode = LineBreakMode.TailTruncation };
        addr.SetBinding(Label.TextProperty, nameof(SourceRowVm.AddressRedacted));
        CenterLabel(addr);
        var status = new Label { FontSize = 12, TextColor = _muted };
        status.SetBinding(Label.TextProperty, nameof(SourceRowVm.StatusText));
        CenterLabel(status);

        var playGreen = Color.FromArgb("#059669");
        var play = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Play, playGreen, Loc.T("Tool.VideoWall.Play"), _card, _line).Host;
        play.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(play, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                BeginPlayback(id);
        });

        var edit = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Edit, _blue, Loc.T("Tool.VideoWall.Editing"), _card, _line).Host;
        edit.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(edit, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                OpenDialog(id);
        });

        var del = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Delete, _warn, Loc.T("Tool.VideoWall.Delete"), _card, _line).Host;
        del.Margin = 0;
        del.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(del, async (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                await DeleteOneAsync(id);
        });

        var row = new Border
        {
            Padding = new Thickness(12, 10),
            StrokeThickness = 0,
            Content = new Grid
            {
                MinimumHeightRequest = 44,
                ColumnDefinitions =
                [
                    new ColumnDefinition(new GridLength(36)),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(88)),
                    new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(72)),
                    new ColumnDefinition(new GridLength(108))
                ],
                ColumnSpacing = 8,
                Children =
                {
                    check,
                    name.Also(v => Grid.SetColumn(v, 1)),
                    kind.Also(v => Grid.SetColumn(v, 2)),
                    addr.Also(v => Grid.SetColumn(v, 3)),
                    status.Also(v => Grid.SetColumn(v, 4)),
                    new HorizontalStackLayout
                    {
                        Spacing = 0,
                        VerticalOptions = LayoutOptions.Center,
                        Children = { play, edit, del }
                    }.Also(v => Grid.SetColumn(v, 5))
                }
            }
        };

        var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        dbl.Tapped += (_, _) =>
        {
            if (row.BindingContext is SourceRowVm vm)
                BeginPlayback(vm.Id);
        };
        row.GestureRecognizers.Add(dbl);
        return row;
    }

    // prefer 步长：默认窗内容区约 3 列；缩放仍按宽算列数（非强制 3）
    private const double CardGap = 8;
    private const double CardMinWidth = 160;
    private const double CardPreferWidth = 200;
    private double _cardWidth = CardPreferWidth;

    private static double ComputeCardWidth(double hostWidth)
    {
        var inner = Math.Max(CardMinWidth, hostWidth - 8);
        var cols = Math.Max(1, (int)Math.Floor((inner + CardGap) / (CardPreferWidth + CardGap)));
        return Math.Clamp(inner / cols - CardGap, CardMinWidth, 280);
    }

    private void ApplyCardWidths()
    {
        foreach (var child in _cardsFlex.Children)
        {
            if (child is not Border b) continue;
            b.WidthRequest = _cardWidth;
            b.MaximumWidthRequest = _cardWidth;
        }
    }

    private View BuildCard()
    {
        var name = new Label { FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = _text, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(SourceRowVm.Name));
        var status = new Label { FontSize = 11, TextColor = _muted };
        status.SetBinding(Label.TextProperty, nameof(SourceRowVm.StatusText));
        var addr = new Label { FontSize = 10, TextColor = _muted, LineBreakMode = LineBreakMode.TailTruncation };
        addr.SetBinding(Label.TextProperty, nameof(SourceRowVm.AddressRedacted));

        var playGreen = Color.FromArgb("#059669");
        var play = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Play, playGreen, Loc.T("Tool.VideoWall.Play"), _card, _line).Host;
        play.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(play, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                BeginPlayback(id);
        });
        var edit = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Edit, _blue, Loc.T("Tool.VideoWall.Editing"), _card, _line).Host;
        edit.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(edit, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                OpenDialog(id);
        });
        var del = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Delete, _warn, Loc.T("Tool.VideoWall.Delete"), _card, _line).Host;
        del.Margin = 0;
        del.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(del, async (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                await DeleteOneAsync(id);
        });

        // 定宽 + 右/下间距；列数随宿主宽变化（默认约 3 列）
        var card = new Border
        {
            WidthRequest = _cardWidth,
            MinimumHeightRequest = 132,
            MaximumWidthRequest = _cardWidth,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Padding = 10,
            Stroke = _line,
            BackgroundColor = _card,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Grid
            {
                RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
                Children =
                {
                    new VerticalStackLayout { Spacing = 4, Children = { name, status, addr } },
                    new HorizontalStackLayout
                    {
                        Spacing = 4,
                        HorizontalOptions = LayoutOptions.End,
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { play, edit, del }
                    }.Also(v => Grid.SetRow(v, 1))
                }
            }
        };

        var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        dbl.Tapped += (_, _) =>
        {
            if (card.BindingContext is SourceRowVm vm)
                BeginPlayback(vm.Id);
        };
        card.GestureRecognizers.Add(dbl);
        return card;
    }

    private async Task DeleteOneAsync(string id)
    {
        if (_service is null) return;
        var page = GetPage();
        if (page is not null)
        {
            var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == id);
            var ok = await page.DisplayAlertAsync(
                Loc.T("Tool.VideoWall.Delete"),
                string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), src?.Name ?? id),
                Loc.T("Tool.VideoWall.Delete"),
                Loc.T("Tool.VideoWall.Cancel"));
            if (!ok) return;
        }

        if (string.Equals(_service.SinglePlayback.CurrentSourceId, id, StringComparison.Ordinal))
            await _player.StopAsync();
            await _service.RemoveSourceAsync(id);
        RefreshAll();
    }

    private Page? GetPage()
        => Application.Current?.Windows.FirstOrDefault()?.Page
           ?? Parent as Page
           ?? this.FindParentPage();

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
        TilePlayState.Resolving => Loc.T("Tool.VideoWall.StatusResolving"),
        TilePlayState.Connecting => Loc.T("Tool.VideoWall.StatusConnecting"),
        TilePlayState.Buffering => Loc.T("Tool.VideoWall.StatusBuffering"),
        TilePlayState.Stalled => Loc.T("Tool.VideoWall.StatusStalled"),
        TilePlayState.Reconnecting => Loc.T("Tool.VideoWall.StatusReconnecting"),
        TilePlayState.Paused => Loc.T("Tool.VideoWall.Pause"),
        TilePlayState.Stopped => Loc.T("Tool.VideoWall.StatusStopped"),
        _ => state.ToString()
        };

    private Button MakeBtn(string text, Color bg, Color fg, Color? border = null)
        => new()
        {
            Text = text,
            BackgroundColor = bg,
            TextColor = fg,
            FontSize = 13,
            Padding = new Thickness(14, 8),
            BorderColor = border ?? Colors.Transparent,
            BorderWidth = border is null ? 0 : 1,
            CornerRadius = 8
        };

    private sealed class SourceRowVm : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string KindText { get; init; } = "";
        public string AddressRedacted { get; init; } = "";
        public string StatusText { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}

file static class VideoWallMauiExt
{
    public static T Also<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }

    public static Page? FindParentPage(this Element element)
    {
        for (var p = element.Parent; p is not null; p = p.Parent)
        {
            if (p is Page page)
                return page;
        }

        return null;
    }
}