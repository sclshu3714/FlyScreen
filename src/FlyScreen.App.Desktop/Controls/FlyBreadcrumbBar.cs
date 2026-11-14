using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlyScreen.Models;

namespace FlyScreen.App.Desktop.Controls;

/// <summary>
/// Avalonia 面包屑：胶囊段 + 箭头分隔，跟随 WinParity 主题资源。
/// </summary>
public sealed class FlyBreadcrumbBar : StackPanel
{
    public event Action<BreadcrumbSegment>? SegmentClicked;
    public event Action<BreadcrumbSegment>? SegmentCloseClicked;

    public FlyBreadcrumbBar()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 0;
    }

    public void SetItems(IReadOnlyList<BreadcrumbSegment> items)
    {
        Children.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                Children.Add(CreateSeparator());
            Children.Add(CreateChip(items[i]));
        }
    }

    private Control CreateSeparator()
        => new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = TryBrush("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
            Opacity = 0.9,
            IsHitTestVisible = false
        };

    private Control CreateChip(BreadcrumbSegment seg)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2
        };

        row.Children.Add(new TextBlock
        {
            Text = seg.Title,
            FontSize = 13,
            FontWeight = seg.IsActive ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = seg.IsActive ? TryBrush("Blue") : TryBrush("Text"),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (seg.ShowClose)
        {
            var closeGlyph = new TextBlock
            {
                Text = "×",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = TryBrush("Muted"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var close = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Child = closeGlyph,
                Tag = "crumb-close",
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center
            };
            close.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SegmentCloseClicked?.Invoke(seg);
            };
            close.PointerEntered += (_, _) =>
            {
                close.Background = TryBrush("BlueSoft");
                close.BorderBrush = TryBrush("Blue");
                closeGlyph.Foreground = TryBrush("Blue");
            };
            close.PointerExited += (_, _) =>
            {
                close.Background = Brushes.Transparent;
                close.BorderBrush = Brushes.Transparent;
                closeGlyph.Foreground = TryBrush("Muted");
            };
            row.Children.Add(close);
        }

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, seg.ShowClose ? 4 : 10, 4),
            Background = seg.IsActive ? TryBrush("BlueSoft") : Brushes.Transparent,
            BorderBrush = seg.IsActive ? TryBrush("Blue") : Brushes.Transparent,
            BorderThickness = new Thickness(seg.IsActive ? 1 : 0),
            Child = row,
            Cursor = seg.IsClickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default
        };

        if (seg.IsClickable)
        {
            chip.PointerPressed += (_, e) =>
            {
                if (e.Handled) return;
                if (e.Source is Control c && IsCloseHit(c))
                    return;
                SegmentClicked?.Invoke(seg);
                e.Handled = true;
            };
            chip.PointerEntered += (_, _) =>
            {
                if (!seg.IsActive)
                    chip.Background = TryBrush("BlueHover");
            };
            chip.PointerExited += (_, _) =>
            {
                chip.Background = seg.IsActive ? TryBrush("BlueSoft") : Brushes.Transparent;
            };
        }

        return chip;
    }

    private static bool IsCloseHit(Control? c)
    {
        while (c != null)
        {
            if (c is Border { Tag: "crumb-close" })
                return true;
            c = c.Parent as Control;
        }
        return false;
    }

    private IBrush TryBrush(string key)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var res) == true
            && res is IBrush b)
            return b;

        return key switch
        {
            "Blue" => new SolidColorBrush(Color.Parse("#2563EB")),
            "BlueSoft" => new SolidColorBrush(Color.Parse("#DBEAFE")),
            "BlueHover" => new SolidColorBrush(Color.Parse("#EFF6FF")),
            "Muted" => new SolidColorBrush(Color.Parse("#6B7280")),
            _ => new SolidColorBrush(Color.Parse("#111827"))
        };
    }
}
