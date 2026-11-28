using FlyScreen.Models;
using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Ui.Maui;

/// <summary>
/// 资源管理器风格路径条：左侧后退/前进/上级/刷新 + 地址栏。
/// </summary>
public sealed class FlyPathBreadcrumbBar : Grid
{
    private readonly Border _address = new();
    private readonly Border _backBtn = new();
    private readonly Border _forwardBtn = new();
    private readonly Border _upBtn = new();
    private readonly Border _refreshBtn = new();
    private readonly Grid _host = new();
    private readonly HorizontalStackLayout _segments = new()
    {
        Spacing = 0,
        VerticalOptions = LayoutOptions.Center
    };
    private readonly BoxView _spacer = new()
    {
        Color = Colors.Transparent,
        HorizontalOptions = LayoutOptions.Fill,
        MinimumWidthRequest = 24
    };
    private readonly Entry _editBox = new()
    {
        IsVisible = false,
        FontSize = 13,
        BackgroundColor = Colors.Transparent
    };
    private readonly Grid _display = new()
    {
        ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(GridLength.Auto),
            new(GridLength.Star)
        }
    };

    private IReadOnlyList<PathBreadcrumbItem> _items = Array.Empty<PathBreadcrumbItem>();
    private bool _editing;
    private string _pathText = "/";
    private double _lastWidth;
    private DateTime _menuOpenedUtc;
    private Grid? _dropHost;
    private Grid? _dropLayer;

    public event Action? BackClicked;
    public event Action? ForwardClicked;
    public event Action? UpClicked;
    public event Action? RefreshClicked;
    public event Action<PathBreadcrumbItem>? SegmentClicked;
    public event Action<string>? PathCommitted;
    public Func<string?, IReadOnlyList<PathBreadcrumbItem>>? SiblingProvider { get; set; }

    /// <summary>地址栏最左侧根图标（资源管理器用电脑图标；保险箱用锁）。</summary>
    public string RootGlyph { get; set; } = "🖥";

    public string PathText
    {
        get => _pathText;
        set
        {
            _pathText = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
            if (_editing)
                _editBox.Text = _pathText;
        }
    }

    public FlyPathBreadcrumbBar()
    {
        HeightRequest = 32;
        ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(GridLength.Auto),
            new(GridLength.Star)
        };

        StyleNav(_backBtn, IconCodes.ChevronLeft, () => { if (_backBtn.IsEnabled) BackClicked?.Invoke(); });
        StyleNav(_forwardBtn, IconCodes.ChevronRight, () => { if (_forwardBtn.IsEnabled) ForwardClicked?.Invoke(); });
        StyleNav(_upBtn, IconCodes.ChevronUp, () => { if (_upBtn.IsEnabled) UpClicked?.Invoke(); });
        StyleNav(_refreshBtn, IconCodes.Refresh, () => { if (_refreshBtn.IsEnabled) RefreshClicked?.Invoke(); });
        var nav = new HorizontalStackLayout
        {
            Spacing = 0,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalOptions = LayoutOptions.Center,
            Children = { _backBtn, _forwardBtn, _upBtn, _refreshBtn }
        };
        Grid.SetColumn((BindableObject)nav, 0);
        Children.Add(nav);

        _address.StrokeShape = new RoundRectangle { CornerRadius = 6 };
        _address.StrokeThickness = 1;
        _address.Padding = new Thickness(4, 0);
        _address.Stroke = TryColor("Line");
        _address.BackgroundColor = TryColor("CardBg");
        Grid.SetColumn((BindableObject)_address, 1);
        Children.Add(_address);

        Grid.SetColumn(_segments, 0);
        Grid.SetColumn(_spacer, 1);
        _display.Children.Add(_segments);
        _display.Children.Add(_spacer);
        _host.Children.Add(_display);
        _host.Children.Add(_editBox);
        _address.Content = _host;

        var spacerTap = new TapGestureRecognizer();
        spacerTap.Tapped += (_, _) => EnterEdit();
        _spacer.GestureRecognizers.Add(spacerTap);

        var barTap = new TapGestureRecognizer();
        barTap.Tapped += (_, _) =>
        {
            if ((DateTime.UtcNow - _menuOpenedUtc).TotalMilliseconds < 300)
                return;
            if (!_editing) EnterEdit();
        };
        _address.GestureRecognizers.Add(barTap);

        _editBox.Completed += (_, _) => CommitEdit();
        _editBox.Unfocused += (_, _) =>
        {
            if (_editing) CancelEdit();
        };

        _address.SizeChanged += (_, _) =>
        {
            if (Math.Abs(_address.Width - _lastWidth) < 1) return;
            _lastWidth = _address.Width;
            if (!_editing) RebuildDisplay();
        };
        SetNavState(false, false, false);
    }

    public void SetItems(IReadOnlyList<PathBreadcrumbItem> items)
    {
        _items = items ?? Array.Empty<PathBreadcrumbItem>();
        _pathText = BuildPathText(_items);
        if (_editing)
        {
            CancelEdit();
            return;
        }
        RebuildDisplay();
    }

    public void SetNavState(bool canBack, bool canForward, bool canUp)
    {
        ApplyNavEnabled(_backBtn, canBack);
        ApplyNavEnabled(_forwardBtn, canForward);
        ApplyNavEnabled(_upBtn, canUp);
        ApplyNavEnabled(_refreshBtn, true);
    }

    public void SetNavTips(string back, string forward, string up, string refresh)
    {
        ToolTipProperties.SetText(_backBtn, back);
        ToolTipProperties.SetText(_forwardBtn, forward);
        ToolTipProperties.SetText(_upBtn, up);
        ToolTipProperties.SetText(_refreshBtn, refresh);
    }

    private void StyleNav(Border host, string iconCode, Action click)
    {
        var path = new ShapePath
        {
            Data = ParseGeometry(IconCatalog.GetPath(iconCode)),
            Aspect = Stretch.Uniform,
            Fill = TryColor("Text"),
            WidthRequest = 14,
            HeightRequest = 14,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };
        host.Content = path;
        host.WidthRequest = 28;
        host.HeightRequest = 28;
        host.Padding = 0;
        host.Margin = new Thickness(0, 0, 2, 0);
        host.BackgroundColor = Colors.Transparent;
        host.StrokeThickness = 0;
        host.StrokeShape = new RoundRectangle { CornerRadius = 4 };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => click();
        host.GestureRecognizers.Add(tap);
    }

    private void ApplyNavEnabled(Border host, bool enabled)
    {
        host.IsEnabled = enabled;
        host.Opacity = enabled ? 1 : 0.35;
        if (host.Content is ShapePath path)
            path.Fill = enabled ? TryColor("Text") : TryColor("Muted");
    }

    private static Geometry? ParseGeometry(string pathData)
    {
        var converter = new PathGeometryConverter();
        return converter.ConvertFromInvariantString(pathData) as Geometry;
    }

    private void RebuildDisplay()
    {
        CloseDrop();
        // 面板已随窗释放时不要再新建 Label 并挂到布局上，否则 ToPlatform 会访问已释放的 DI 上下文。
        if (Handler is null) return;
        _address.Stroke = TryColor("Line");
        _address.BackgroundColor = TryColor("CardBg");
        var visibleStart = ComputeVisibleStart();
        _segments.Children.Clear();
        _segments.Children.Add(CreateRootIcon());

        var display = new List<(PathBreadcrumbItem? Item, bool IsOverflow)>();
        if (visibleStart > 0)
            display.Add((null, true));

        for (var i = visibleStart; i < _items.Count; i++)
        {
            var it = _items[i];
            // 仅跳过纯路径根 "/"；可读根名（如「保险箱」）要显示
            if (IsRootToken(it)) continue;
            display.Add((it, false));
        }

        for (var i = 0; i < display.Count; i++)
        {
            var (item, isOverflow) = display[i];
            if (isOverflow)
            {
                var hidden = _items.Take(visibleStart).Where(x => !IsRootToken(x)).ToList();
                _segments.Children.Add(CreateChevron(null, hidden));
                _segments.Children.Add(CreateOverflowChip(hidden));
            }
            else
            {
                _segments.Children.Add(CreateChevron(item!.ParentId));
                _segments.Children.Add(CreateSegmentChip(item, i == display.Count - 1));
            }
        }
    }

    private int ComputeVisibleStart()
    {
        var avail = _address.Width > 0 ? _address.Width : _lastWidth;
        if (avail <= 0 || _items.Count == 0) return 0;

        var budget = Math.Max(40, avail - 44);
        const double chevron = 18;
        const double overflowChip = 28;

        var widths = new double[_items.Count];
        for (var i = 0; i < _items.Count; i++)
        {
            if (IsRootToken(_items[i])) { widths[i] = 0; continue; }
            widths[i] = Math.Max(24, _items[i].Title.Length * 8.0 + 16) + chevron;
        }

        var used = 0.0;
        var start = _items.Count;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (widths[i] <= 0) { start = i; continue; }
            var withOverflow = i > 0 ? overflowChip + chevron : 0;
            if (used + widths[i] + withOverflow > budget && start < _items.Count)
                break;
            used += widths[i];
            start = i;
        }
        return Math.Clamp(start, 0, Math.Max(0, _items.Count - 1));
    }

    private View CreateRootIcon()
    {
        var rootItem = _items.FirstOrDefault(i => i.Id == null)
                       ?? _items.FirstOrDefault(IsRootToken)
                       ?? new PathBreadcrumbItem { Id = null, Title = "/", ParentId = null };
        var label = new Label
        {
            Text = RootGlyph,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        var host = new Border
        {
            WidthRequest = 26,
            HeightRequest = 26,
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = label,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Margin = new Thickness(2, 0, 0, 0)
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => SegmentClicked?.Invoke(rootItem);
        host.GestureRecognizers.Add(tap);
        return host;
    }

    private View CreateSegmentChip(PathBreadcrumbItem item, bool isLast)
    {
        var label = new Label
        {
            Text = item.Title,
            FontSize = 13,
            FontAttributes = isLast ? FontAttributes.Bold : FontAttributes.None,
            TextColor = TryColor("Text"),
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(6, 0)
        };
        var chip = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = label,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(0, 2)
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => SegmentClicked?.Invoke(item);
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    private View CreateOverflowChip(IReadOnlyList<PathBreadcrumbItem> hidden)
    {
        var label = new Label
        {
            Text = "…",
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = TryColor("Text"),
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(6, 0)
        };
        var chip = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = label,
            StrokeShape = new RoundRectangle { CornerRadius = 4 }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _menuOpenedUtc = DateTime.UtcNow;
            ShowMenu(chip, hidden);
        };
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    private View CreateChevron(string? parentId, IReadOnlyList<PathBreadcrumbItem>? overflowHidden = null)
    {
        var label = new Label
        {
            Text = "›",
            FontSize = 16,
            TextColor = TryColor("Muted"),
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(2, 0)
        };
        var chip = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = label,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            MinimumWidthRequest = 16,
            Padding = new Thickness(2)
        };
        if (SiblingProvider != null || overflowHidden != null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _menuOpenedUtc = DateTime.UtcNow;
                if (overflowHidden != null)
                {
                    ShowMenu(chip, overflowHidden);
                    return;
                }
                var siblings = SiblingProvider?.Invoke(parentId) ?? Array.Empty<PathBreadcrumbItem>();
                ShowMenu(chip, siblings);
            };
            chip.GestureRecognizers.Add(tap);
        }
        return chip;
    }

    private void ShowMenu(View anchor, IReadOnlyList<PathBreadcrumbItem> items)
    {
        CloseDrop();
        var host = FindOverlayHost();
        if (host == null)
            return;

        var origin = OffsetIn(anchor, host);
        var rowH = 32d;
        var menuH = Math.Max(1, items.Count) * rowH + 8;
        var menuW = 160d;
        if (items.Count > 0)
            menuW = Math.Clamp(items.Max(i => i.Title.Length) * 9d + 32, 140, 280);

        var x = origin.X;
        var y = origin.Y + Math.Max(anchor.Height, 20);
        if (host.Width > 0 && x + menuW > host.Width - 8)
            x = Math.Max(8, host.Width - menuW - 8);
        if (host.Height > 0 && y + menuH > host.Height - 8)
            y = Math.Max(8, origin.Y - menuH);

        var list = new VerticalStackLayout { Spacing = 0 };
        if (items.Count == 0)
        {
            list.Children.Add(new Label
            {
                Text = "(空)",
                TextColor = TryColor("Muted"),
                FontSize = 13,
                Margin = new Thickness(12, 8)
            });
        }
        else
        {
            foreach (var it in items)
            {
                var captured = it;
                var row = new Label
                {
                    Text = captured.Title,
                    TextColor = TryColor("Text"),
                    FontSize = 13,
                    Margin = new Thickness(12, 0),
                    VerticalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation
                };
                var hit = new Border
                {
                    HeightRequest = rowH,
                    BackgroundColor = Colors.Transparent,
                    StrokeThickness = 0,
                    Content = row
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) =>
                {
                    CloseDrop();
                    SegmentClicked?.Invoke(captured);
                };
                hit.GestureRecognizers.Add(tap);
                list.Children.Add(hit);
            }
        }

        var card = new Border
        {
            BackgroundColor = TryColor("CardBg"),
            Stroke = TryColor("Line"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(0, 4),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            WidthRequest = menuW,
            Content = list,
            Margin = new Thickness(x, y, 0, 0)
        };

        var scrim = new BoxView
        {
            Color = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => CloseDrop();
        scrim.GestureRecognizers.Add(scrimTap);

        var layer = new Grid
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        layer.Children.Add(scrim);
        layer.Children.Add(card);
        if (host.RowDefinitions.Count > 0)
            Grid.SetRowSpan(layer, host.RowDefinitions.Count);
        if (host.ColumnDefinitions.Count > 0)
            Grid.SetColumnSpan(layer, host.ColumnDefinitions.Count);

        host.Children.Add(layer);
        _dropHost = host;
        _dropLayer = layer;
    }

    private void CloseDrop()
    {
        if (_dropLayer != null && _dropHost != null && _dropHost.Children.Contains(_dropLayer))
            _dropHost.Children.Remove(_dropLayer);
        _dropLayer = null;
        _dropHost = null;
    }

    private Grid? FindOverlayHost()
    {
        Grid? host = null;
        for (Element? e = this; e != null; e = e.Parent)
        {
            if (e is Grid g)
                host = g;
        }
        return host;
    }

    private static Point OffsetIn(VisualElement from, VisualElement host)
    {
        double x = 0, y = 0;
        for (Element? e = from; e != null && !ReferenceEquals(e, host); e = e.Parent)
        {
            if (e is VisualElement ve)
            {
                x += ve.X;
                y += ve.Y;
            }
        }
        return new Point(x, y);
    }

    private void EnterEdit()
    {
        if (_editing) return;
        _editing = true;
        _display.IsVisible = false;
        _editBox.IsVisible = true;
        _editBox.Text = _pathText;
        _editBox.Focus();
    }

    private void CancelEdit()
    {
        if (!_editing) return;
        _editing = false;
        _editBox.IsVisible = false;
        _display.IsVisible = true;
        RebuildDisplay();
    }

    private void CommitEdit()
    {
        if (!_editing) return;
        var text = _editBox.Text?.Trim() ?? "/";
        _editing = false;
        _editBox.IsVisible = false;
        _display.IsVisible = true;
        PathCommitted?.Invoke(text);
        RebuildDisplay();
    }

    private static bool IsRootToken(PathBreadcrumbItem i)
        => i.Id == null && (i.Title == "/" || i.Title == "\\");

    private static string BuildPathText(IReadOnlyList<PathBreadcrumbItem> items)
    {
        var parts = items.Where(i => !IsRootToken(i)).Select(i => i.Title).ToList();
        return parts.Count == 0 ? "/" : "/" + string.Join("/", parts);
    }

    private static Color TryColor(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true)
        {
            if (v is Color c) return c;
            if (v is SolidColorBrush b) return b.Color;
        }
        return key switch
        {
            "Blue" => Color.FromArgb("#2563EB"),
            "Muted" => Color.FromArgb("#6B7280"),
            "Line" => Color.FromArgb("#E5E7EB"),
            "CardBg" => Color.FromArgb("#FFFFFF"),
            _ => Color.FromArgb("#111827")
        };
    }
}
