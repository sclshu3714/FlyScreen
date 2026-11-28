using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FlyScreen.Models;
using FlyScreen.Ui.Theme.Icons;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace FlyScreen.Ui.Wpf;

/// <summary>
/// 资源管理器风格路径条：左侧后退/前进/上级/刷新 + 地址栏（段点击、› 菜单、… 折叠、空白编辑）。
/// </summary>
public sealed class FlyPathBreadcrumbBar : Grid
{
    private readonly Button _backBtn;
    private readonly Button _forwardBtn;
    private readonly Button _upBtn;
    private readonly Button _refreshBtn;
    private readonly Border _address = new();
    private readonly Grid _addressHost = new();
    private readonly DockPanel _display = new() { LastChildFill = true };
    private readonly StackPanel _segments = new()
    {
        Orientation = WpfOrientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _spacer = new()
    {
        Background = WpfBrushes.Transparent,
        MinWidth = 24,
        Cursor = WpfCursors.IBeam
    };
    private readonly TextBox _editBox = new()
    {
        Visibility = Visibility.Collapsed,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0),
        Background = WpfBrushes.Transparent,
        Padding = new Thickness(6, 0, 6, 0),
        FontSize = 13
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

    /// <summary>地址栏根图标几何；保险箱可换锁形。</summary>
    public string? RootIconGeometry { get; set; }

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
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        _backBtn = NavBtn(IconCodes.ChevronLeft, () => BackClicked?.Invoke());
        _forwardBtn = NavBtn(IconCodes.ChevronRight, () => ForwardClicked?.Invoke());
        _upBtn = NavBtn(IconCodes.ChevronUp, () => UpClicked?.Invoke());
        _refreshBtn = NavBtn(IconCodes.Refresh, () => RefreshClicked?.Invoke());
        nav.Children.Add(_backBtn);
        nav.Children.Add(_forwardBtn);
        nav.Children.Add(_upBtn);
        nav.Children.Add(_refreshBtn);
        SetColumn(nav, 0);
        Children.Add(nav);

        _address.CornerRadius = new CornerRadius(6);
        _address.BorderThickness = new Thickness(1);
        _address.Padding = new Thickness(4, 0, 4, 0);
        _address.SnapsToDevicePixels = true;
        _address.Cursor = WpfCursors.IBeam;
        _address.Focusable = true;
        SetColumn(_address, 1);
        Children.Add(_address);

        Loaded += (_, _) => ApplyChrome();
        _address.SizeChanged += (_, _) =>
        {
            if (!_editing) RebuildDisplay();
        };

        _spacer.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            EnterEdit();
        };
        _address.MouseLeftButtonDown += OnBarMouseDown;

        _editBox.KeyDown += EditBox_KeyDown;
        _editBox.LostKeyboardFocus += (_, _) =>
        {
            if (_editing) CancelEdit();
        };

        DockPanel.SetDock(_segments, Dock.Left);
        _display.Children.Add(_segments);
        _display.Children.Add(_spacer);
        _addressHost.Children.Add(_display);
        _addressHost.Children.Add(_editBox);
        _address.Child = _addressHost;
        ApplyChrome();
        SetNavState(canBack: false, canForward: false, canUp: false);
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
        _backBtn.ToolTip = back;
        _forwardBtn.ToolTip = forward;
        _upBtn.ToolTip = up;
        _refreshBtn.ToolTip = refresh;
    }

    private Button NavBtn(string iconCode, Action click)
    {
        var path = new Path
        {
            Data = Geometry.Parse(IconCatalog.GetPath(iconCode)),
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14,
            Fill = TryBrush("Text"),
            IsHitTestVisible = false
        };
        var b = new Button
        {
            Content = path,
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 2, 0),
            Padding = new Thickness(0),
            Cursor = WpfCursors.Hand,
            Focusable = false,
            BorderThickness = new Thickness(0),
            Background = WpfBrushes.Transparent
        };
        b.Click += (_, _) => click();
        b.MouseEnter += (_, _) =>
        {
            if (b.IsEnabled)
                b.Background = new SolidColorBrush(WpfColor.FromArgb(0x33, 0x88, 0x88, 0x88));
        };
        b.MouseLeave += (_, _) => b.Background = WpfBrushes.Transparent;
        return b;
    }

    private void ApplyNavEnabled(Button b, bool enabled)
    {
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1 : 0.35;
        if (b.Content is Path path)
            path.Fill = enabled ? TryBrush("Text") : TryBrush("Muted");
    }

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editing) return;
        if (!e.Handled)
        {
            e.Handled = true;
            EnterEdit();
        }
    }

    private void ApplyChrome()
    {
        _address.Background = TryBrush("CardBg");
        _address.BorderBrush = TryBrush("Line");
        _editBox.Foreground = TryBrush("Text");
        _editBox.CaretBrush = TryBrush("Blue");
        ApplyNavEnabled(_backBtn, _backBtn.IsEnabled);
        ApplyNavEnabled(_forwardBtn, _forwardBtn.IsEnabled);
        ApplyNavEnabled(_upBtn, _upBtn.IsEnabled);
        ApplyNavEnabled(_refreshBtn, _refreshBtn.IsEnabled);
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
            if (IsRootToken(it))
                continue;
            display.Add((it, false));
        }

        for (var i = 0; i < display.Count; i++)
        {
            var (item, isOverflow) = display[i];
            if (isOverflow)
            {
                var hidden = _items.Take(visibleStart).Where(x => !IsRootToken(x)).ToList();
                _segments.Children.Add(CreateChevron(parentId: null, overflowHidden: hidden));
                _segments.Children.Add(CreateOverflowChip(hidden));
            }
            else
            {
                _segments.Children.Add(CreateChevron(item!.ParentId));
                _segments.Children.Add(CreateSegmentChip(item, isLast: i == display.Count - 1));
            }
        }
    }

    private int ComputeVisibleStart()
    {
        var avail = _address.ActualWidth;
        if (avail <= 0 || double.IsNaN(avail) || _items.Count == 0)
            return 0;

        const double icon = 28;
        const double chevron = 18;
        const double overflowChip = 28;
        const double pad = 16;
        var budget = Math.Max(40, avail - icon - pad);

        var widths = new double[_items.Count];
        for (var i = 0; i < _items.Count; i++)
        {
            if (IsRootToken(_items[i]))
            {
                widths[i] = 0;
                continue;
            }
            widths[i] = Math.Max(24, MeasureText(_items[i].Title) + 16) + chevron;
        }

        var used = 0.0;
        var start = _items.Count;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (widths[i] <= 0)
            {
                start = i;
                continue;
            }
            var withOverflow = i > 0 ? overflowChip + chevron : 0;
            if (used + widths[i] + withOverflow > budget && start < _items.Count)
                break;
            used += widths[i];
            start = i;
        }

        return Math.Clamp(start, 0, Math.Max(0, _items.Count - 1));
    }

    private double MeasureText(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 13 };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    private FrameworkElement CreateRootIcon()
    {
        var geo = string.IsNullOrWhiteSpace(RootIconGeometry)
            ? "M2,6 L2,22 L22,22 L22,8 L12,8 L10,6 Z"
            : RootIconGeometry;
        var path = new Path
        {
            Data = Geometry.Parse(geo),
            Fill = TryBrush("Muted"),
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14,
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var rootItem = _items.FirstOrDefault(i => i.Id == null)
                       ?? _items.FirstOrDefault(IsRootToken)
                       ?? new PathBreadcrumbItem { Id = null, Title = "/", ParentId = null };
        var host = new Border
        {
            Width = 24,
            Height = 24,
            Child = path,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
            CornerRadius = new CornerRadius(4)
        };
        host.MouseLeftButtonDown += (_, e) => e.Handled = true;
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SegmentClicked?.Invoke(rootItem);
        };
        host.MouseEnter += (_, _) => host.Background = TryBrush("BlueHover");
        host.MouseLeave += (_, _) => host.Background = WpfBrushes.Transparent;
        return host;
    }

    private FrameworkElement CreateSegmentChip(PathBreadcrumbItem item, bool isLast)
    {
        var label = new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = TryBrush("Text"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = WpfBrushes.Transparent,
            Child = label,
            Cursor = WpfCursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 2, 0, 2)
        };
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SegmentClicked?.Invoke(item);
        };
        chip.MouseEnter += (_, _) => chip.Background = TryBrush("BlueHover");
        chip.MouseLeave += (_, _) => chip.Background = WpfBrushes.Transparent;
        return chip;
    }

    private FrameworkElement CreateOverflowChip(IReadOnlyList<PathBreadcrumbItem> hidden)
    {
        var label = new TextBlock
        {
            Text = "…",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("Text"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = WpfBrushes.Transparent,
            Child = label,
            Cursor = WpfCursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 2, 0, 2)
        };
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(chip, hidden);
        };
        chip.MouseEnter += (_, _) => chip.Background = TryBrush("BlueHover");
        chip.MouseLeave += (_, _) => chip.Background = WpfBrushes.Transparent;
        return chip;
    }

    private FrameworkElement CreateChevron(string? parentId, IReadOnlyList<PathBreadcrumbItem>? overflowHidden = null)
    {
        var glyph = new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = TryBrush("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0)
        };
        var canMenu = SiblingProvider != null || overflowHidden != null;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = WpfBrushes.Transparent,
            Child = glyph,
            Cursor = canMenu ? WpfCursors.Hand : WpfCursors.Arrow,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 2, 2, 2),
            MinWidth = 16
        };
        if (canMenu)
        {
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) =>
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
            chip.MouseEnter += (_, _) => chip.Background = TryBrush("BlueHover");
            chip.MouseLeave += (_, _) => chip.Background = WpfBrushes.Transparent;
        }
        return chip;
    }

    private void ShowMenu(FrameworkElement anchor, IReadOnlyList<PathBreadcrumbItem> items)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom
        };
        if (items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(空)", IsEnabled = false });
        }
        else
        {
            foreach (var it in items)
            {
                var mi = new MenuItem { Header = it.Title, Tag = it };
                mi.Click += (_, _) =>
                {
                    if (mi.Tag is PathBreadcrumbItem seg)
                        SegmentClicked?.Invoke(seg);
                };
                menu.Items.Add(mi);
            }
        }
        menu.IsOpen = true;
    }

    private void EnterEdit()
    {
        if (_editing) return;
        _editing = true;
        _display.Visibility = Visibility.Collapsed;
        _editBox.Visibility = Visibility.Visible;
        _editBox.Text = _pathText;
        _editBox.SelectAll();
        _editBox.Focus();
        Keyboard.Focus(_editBox);
    }

    private void CancelEdit()
    {
        if (!_editing) return;
        _editing = false;
        _editBox.Visibility = Visibility.Collapsed;
        _display.Visibility = Visibility.Visible;
        RebuildDisplay();
    }

    private void CommitEdit()
    {
        if (!_editing) return;
        var text = _editBox.Text?.Trim() ?? "/";
        _editing = false;
        _editBox.Visibility = Visibility.Collapsed;
        _display.Visibility = Visibility.Visible;
        PathCommitted?.Invoke(text);
        RebuildDisplay();
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
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
    }

    private static bool IsRootToken(PathBreadcrumbItem i)
        => i.Id == null && (i.Title == "/" || i.Title == "\\");

    private static string BuildPathText(IReadOnlyList<PathBreadcrumbItem> items)
    {
        var parts = items.Where(i => !IsRootToken(i)).Select(i => i.Title).ToList();
        return parts.Count == 0 ? "/" : "/" + string.Join("/", parts);
    }

    private Brush TryBrush(string key)
    {
        if (TryFindResource(key) is Brush local)
            return local;
        var win = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        if (win?.TryFindResource(key) is Brush fromWin)
            return fromWin;
        if (Application.Current?.TryFindResource(key) is Brush fromApp)
            return fromApp;
        return key switch
        {
            "Blue" => new SolidColorBrush(WpfColor.FromRgb(0x25, 0x63, 0xEB)),
            "BlueSoft" => new SolidColorBrush(WpfColor.FromRgb(0xDB, 0xEA, 0xFE)),
            "BlueHover" => new SolidColorBrush(WpfColor.FromRgb(0xEF, 0xF6, 0xFF)),
            "Muted" => new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x72, 0x80)),
            "Line" => new SolidColorBrush(WpfColor.FromRgb(0xE5, 0xE7, 0xEB)),
            "CardBg" => new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF)),
            _ => new SolidColorBrush(WpfColor.FromRgb(0x11, 0x18, 0x27))
        };
    }
}
