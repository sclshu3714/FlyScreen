using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Models;
using FlyScreen.Ui.Theme.Icons;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FlyScreen.Ui.Avalonia;

/// <summary>
/// 资源管理器风格路径条：左侧后退/前进/上级/刷新 + 地址栏。
/// </summary>
public sealed class FlyPathBreadcrumbBar : Grid
{
    private readonly Border _address = new();
    private readonly Button _backBtn = new();
    private readonly Button _forwardBtn = new();
    private readonly Button _upBtn = new();
    private readonly Button _refreshBtn = new();
    private readonly Panel _host = new();
    private readonly DockPanel _display = new() { LastChildFill = true };
    private readonly StackPanel _segments = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = 0
    };
    private readonly Border _spacer = new()
    {
        Background = Brushes.Transparent,
        MinWidth = 24,
        Cursor = new Cursor(StandardCursorType.Ibeam)
    };
    private readonly TextBox _editBox = new()
    {
        IsVisible = false,
        FontSize = 13,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(6, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private IReadOnlyList<PathBreadcrumbItem> _items = Array.Empty<PathBreadcrumbItem>();
    private bool _editing;
    private string _pathText = "/";

    public event Action? BackClicked;
    public event Action? ForwardClicked;
    public event Action? UpClicked;
    public event Action? RefreshClicked;
    public event Action<PathBreadcrumbItem>? SegmentClicked;
    public event Action<string>? PathCommitted;
    public Func<string?, IReadOnlyList<PathBreadcrumbItem>>? SiblingProvider { get; set; }

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
        Height = 32;
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        StyleNav(_backBtn, IconCodes.ChevronLeft, () => BackClicked?.Invoke());
        StyleNav(_forwardBtn, IconCodes.ChevronRight, () => ForwardClicked?.Invoke());
        StyleNav(_upBtn, IconCodes.ChevronUp, () => UpClicked?.Invoke());
        StyleNav(_refreshBtn, IconCodes.Refresh, () => RefreshClicked?.Invoke());
        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _backBtn, _forwardBtn, _upBtn, _refreshBtn }
        };
        SetColumn(nav, 0);
        Children.Add(nav);

        _address.CornerRadius = new CornerRadius(6);
        _address.BorderThickness = new Thickness(1);
        _address.Padding = new Thickness(4, 0);
        _address.Cursor = new Cursor(StandardCursorType.Ibeam);
        SetColumn(_address, 1);
        Children.Add(_address);
        ApplyChrome();

        DockPanel.SetDock(_segments, Dock.Left);
        _display.Children.Add(_segments);
        _display.Children.Add(_spacer);
        _host.Children.Add(_display);
        _host.Children.Add(_editBox);
        _address.Child = _host;

        _spacer.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            EnterEdit();
        };
        _address.PointerPressed += (_, e) =>
        {
            if (_editing || e.Handled) return;
            e.Handled = true;
            EnterEdit();
        };
        _address.SizeChanged += (_, _) =>
        {
            if (!_editing) RebuildDisplay();
        };

        _editBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitEdit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEdit();
            }
        };
        _editBox.LostFocus += (_, _) =>
        {
            if (_editing) CancelEdit();
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
        ToolTip.SetTip(_backBtn, back);
        ToolTip.SetTip(_forwardBtn, forward);
        ToolTip.SetTip(_upBtn, up);
        ToolTip.SetTip(_refreshBtn, refresh);
    }

    private void StyleNav(Button b, string iconCode, Action click)
    {
        var path = new ShapePath
        {
            Data = StreamGeometry.Parse(IconCatalog.GetPath(iconCode)),
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14,
            Fill = TryBrush("Text"),
            IsHitTestVisible = false
        };
        b.Content = path;
        b.Width = 28;
        b.Height = 28;
        b.Padding = new Thickness(0);
        b.Margin = new Thickness(0, 0, 2, 0);
        b.Background = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.Click += (_, _) => click();
    }

    private void ApplyNavEnabled(Button b, bool enabled)
    {
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1 : 0.35;
        if (b.Content is ShapePath path)
            path.Fill = enabled ? TryBrush("Text") : TryBrush("Muted");
    }

    private void ApplyChrome()
    {
        _address.Background = TryBrush("CardBg");
        _address.BorderBrush = TryBrush("Line");
        _editBox.Foreground = TryBrush("Text");
        _editBox.CaretBrush = TryBrush("Blue");
    }

    private void RebuildDisplay()
    {
        ApplyChrome();
        var visibleStart = ComputeVisibleStart();
        _segments.Children.Clear();
        _segments.Children.Add(CreateRootIcon());

        var display = new List<(PathBreadcrumbItem? Item, bool IsOverflow)>();
        if (visibleStart > 0)
            display.Add((null, true));

        for (var i = visibleStart; i < _items.Count; i++)
        {
            var it = _items[i];
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
        var avail = _address.Bounds.Width;
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

    private Control CreateRootIcon()
    {
        var rootItem = _items.FirstOrDefault(i => i.Id == null)
                       ?? _items.FirstOrDefault(IsRootToken)
                       ?? new PathBreadcrumbItem { Id = null, Title = "/", ParentId = null };
        var host = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = RootGlyph,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        host.PointerPressed += (_, e) => e.Handled = true;
        host.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            SegmentClicked?.Invoke(rootItem);
        };
        host.PointerEntered += (_, _) => host.Background = TryBrush("BlueHover");
        host.PointerExited += (_, _) => host.Background = Brushes.Transparent;
        return host;
    }

    private Control CreateSegmentChip(PathBreadcrumbItem item, bool isLast)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(0, 2),
            Child = new TextBlock
            {
                Text = item.Title,
                FontSize = 13,
                FontWeight = isLast ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = TryBrush("Text"),
                Margin = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chip.PointerPressed += (_, e) => e.Handled = true;
        chip.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            SegmentClicked?.Invoke(item);
        };
        chip.PointerEntered += (_, _) => chip.Background = TryBrush("BlueHover");
        chip.PointerExited += (_, _) => chip.Background = Brushes.Transparent;
        return chip;
    }

    private Control CreateOverflowChip(IReadOnlyList<PathBreadcrumbItem> hidden)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "…",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = TryBrush("Text"),
                Margin = new Thickness(6, 0),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chip.PointerPressed += (_, e) => e.Handled = true;
        chip.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(chip, hidden);
        };
        chip.PointerEntered += (_, _) => chip.Background = TryBrush("BlueHover");
        chip.PointerExited += (_, _) => chip.Background = Brushes.Transparent;
        return chip;
    }

    private Control CreateChevron(string? parentId, IReadOnlyList<PathBreadcrumbItem>? overflowHidden = null)
    {
        var canMenu = SiblingProvider != null || overflowHidden != null;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            MinWidth = 16,
            Padding = new Thickness(2),
            Cursor = canMenu ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Child = new TextBlock
            {
                Text = "›",
                FontSize = 16,
                Foreground = TryBrush("Muted"),
                Margin = new Thickness(2, 0),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (canMenu)
        {
            chip.PointerPressed += (_, e) => e.Handled = true;
            chip.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                if (overflowHidden != null)
                {
                    ShowMenu(chip, overflowHidden);
                    return;
                }
                var siblings = SiblingProvider?.Invoke(parentId) ?? Array.Empty<PathBreadcrumbItem>();
                ShowMenu(chip, siblings);
            };
            chip.PointerEntered += (_, _) => chip.Background = TryBrush("BlueHover");
            chip.PointerExited += (_, _) => chip.Background = Brushes.Transparent;
        }
        return chip;
    }

    private void ShowMenu(Control anchor, IReadOnlyList<PathBreadcrumbItem> items)
    {
        var flyout = new MenuFlyout();
        if (items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(空)", IsEnabled = false });
        }
        else
        {
            foreach (var it in items)
            {
                var mi = new MenuItem { Header = it.Title };
                mi.Click += (_, _) => SegmentClicked?.Invoke(it);
                flyout.Items.Add(mi);
            }
        }
        flyout.ShowAt(anchor, true);
    }

    private void EnterEdit()
    {
        if (_editing) return;
        _editing = true;
        _display.IsVisible = false;
        _editBox.IsVisible = true;
        _editBox.Text = _pathText;
        _editBox.Focus();
        _editBox.SelectAll();
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

    private IBrush TryBrush(string key)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var res) == true
            && res is IBrush b)
            return b;
        return key switch
        {
            "Blue" => new SolidColorBrush(Color.Parse("#2563EB")),
            "BlueHover" => new SolidColorBrush(Color.Parse("#EFF6FF")),
            "Muted" => new SolidColorBrush(Color.Parse("#6B7280")),
            "Line" => new SolidColorBrush(Color.Parse("#E5E7EB")),
            "CardBg" => new SolidColorBrush(Color.Parse("#FFFFFF")),
            _ => new SolidColorBrush(Color.Parse("#111827"))
        };
    }
}
