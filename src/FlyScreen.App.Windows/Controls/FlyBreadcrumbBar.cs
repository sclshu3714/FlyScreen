using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Models;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;

namespace FlyScreen.Controls;

/// <summary>
/// 自定义面包屑：胶囊段 + 箭头分隔，走 DynamicResource 主题色，与工具页/网络页共用。
/// </summary>
public sealed class FlyBreadcrumbBar : StackPanel
{
    public event Action<BreadcrumbSegment>? SegmentClicked;
    public event Action<BreadcrumbSegment>? SegmentCloseClicked;

    public FlyBreadcrumbBar()
    {
        Orientation = WpfOrientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
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

    private FrameworkElement CreateSeparator()
    {
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 5,5 L 0,10"),
            Stroke = TryBrush("Muted"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 6,
            Height = 11,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            Opacity = 0.85,
            IsHitTestVisible = false
        };
    }

    private FrameworkElement CreateChip(BreadcrumbSegment seg)
    {
        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = seg.Title,
            FontSize = 13,
            FontWeight = seg.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = seg.IsActive ? TryBrush("Blue") : TryBrush("Text"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = seg.ShowClose ? new Thickness(0, 0, 2, 0) : new Thickness(0)
        };

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, seg.ShowClose ? 4 : 10, 4),
            Background = seg.IsActive ? TryBrush("BlueSoft") : WpfBrushes.Transparent,
            BorderBrush = seg.IsActive ? TryBrush("Blue") : WpfBrushes.Transparent,
            BorderThickness = new Thickness(seg.IsActive ? 1 : 0),
            Cursor = seg.IsClickable ? WpfCursors.Hand : WpfCursors.Arrow,
            Child = row,
            Tag = seg,
            SnapsToDevicePixels = true
        };

        row.Children.Add(label);

        if (seg.ShowClose)
        {
            var closeGlyph = new TextBlock
            {
                Text = "×",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TryBrush("Muted"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var close = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(3),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = WpfCursors.Hand,
                Child = closeGlyph,
                Tag = "crumb-close",
                ToolTip = "关闭",
                VerticalAlignment = VerticalAlignment.Center
            };
            close.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SegmentCloseClicked?.Invoke(seg);
            };
            close.MouseEnter += (_, _) =>
            {
                close.Background = TryBrush("BlueSoft");
                close.BorderBrush = TryBrush("Blue");
                closeGlyph.Foreground = TryBrush("Blue");
            };
            close.MouseLeave += (_, _) =>
            {
                close.Background = WpfBrushes.Transparent;
                close.BorderBrush = WpfBrushes.Transparent;
                closeGlyph.Foreground = TryBrush("Muted");
            };
            row.Children.Add(close);
        }

        if (seg.IsClickable)
        {
            chip.MouseLeftButtonUp += (_, e) =>
            {
                if (IsCloseHit(e.OriginalSource as DependencyObject))
                    return;
                if (chip.Tag is BreadcrumbSegment s)
                    SegmentClicked?.Invoke(s);
                e.Handled = true;
            };
            chip.MouseEnter += (_, _) =>
            {
                if (seg.IsActive) return;
                chip.Background = TryBrush("BlueHover");
            };
            chip.MouseLeave += (_, _) =>
            {
                chip.Background = seg.IsActive ? TryBrush("BlueSoft") : WpfBrushes.Transparent;
            };
        }

        return chip;
    }

    private static bool IsCloseHit(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { Tag: "crumb-close" })
                return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private System.Windows.Media.Brush TryBrush(string key)
    {
        // 禁止 FindResource：未入树时会抛「未找到 * 资源」并弹未处理错误框
        if (TryFindResource(key) is System.Windows.Media.Brush local)
            return local;

        var win = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        if (win?.TryFindResource(key) is System.Windows.Media.Brush fromWin)
            return fromWin;

        if (Application.Current?.TryFindResource(key) is System.Windows.Media.Brush fromApp)
            return fromApp;

        return key switch
        {
            "Blue" => new SolidColorBrush(WpfColor.FromRgb(0x25, 0x63, 0xEB)),
            "BlueSoft" => new SolidColorBrush(WpfColor.FromRgb(0xDB, 0xEA, 0xFE)),
            "BlueHover" => new SolidColorBrush(WpfColor.FromRgb(0xEF, 0xF6, 0xFF)),
            "Muted" => new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x72, 0x80)),
            _ => new SolidColorBrush(WpfColor.FromRgb(0x11, 0x18, 0x27))
        };
    }
}
