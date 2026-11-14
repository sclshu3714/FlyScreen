using FlyScreen.Models;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui.Controls;

/// <summary>
/// MAUI 面包屑：胶囊段 + 箭头分隔，DynamicResource 跟随系统主题。
/// </summary>
public sealed class FlyBreadcrumbBar : HorizontalStackLayout
{
    public event Action<BreadcrumbSegment>? SegmentClicked;
    public event Action<BreadcrumbSegment>? SegmentCloseClicked;

    public FlyBreadcrumbBar()
    {
        Spacing = 0;
        VerticalOptions = LayoutOptions.Center;
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

    private static View CreateSeparator()
        => new Label
        {
            Text = "›",
            FontSize = 16,
            TextColor = TryColor("Muted"),
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(6, 0),
            Opacity = 0.9
        };

    private View CreateChip(BreadcrumbSegment seg)
    {
        var row = new HorizontalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center
        };

        var label = new Label
        {
            Text = seg.Title,
            FontSize = 13,
            FontAttributes = seg.IsActive ? FontAttributes.Bold : FontAttributes.None,
            TextColor = seg.IsActive ? TryColor("Blue") : TryColor("Text"),
            VerticalOptions = LayoutOptions.Center
        };
        row.Children.Add(label);

        if (seg.ShowClose)
        {
            var closeGlyph = new Label
            {
                Text = "×",
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = TryColor("Muted"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };
            var close = new Border
            {
                WidthRequest = 16,
                HeightRequest = 16,
                Margin = new Thickness(2, 0, 0, 0),
                StrokeShape = new RoundRectangle { CornerRadius = 3 },
                BackgroundColor = Colors.Transparent,
                Stroke = Colors.Transparent,
                StrokeThickness = 1,
                Content = closeGlyph,
                VerticalOptions = LayoutOptions.Center
            };
            var closeTap = new TapGestureRecognizer();
            closeTap.Tapped += (_, _) => SegmentCloseClicked?.Invoke(seg);
            close.GestureRecognizers.Add(closeTap);
            row.Children.Add(close);
        }

        var chip = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(10, 4, seg.ShowClose ? 4 : 10, 4),
            BackgroundColor = seg.IsActive ? TryColor("BlueSoft") : Colors.Transparent,
            Stroke = seg.IsActive ? TryColor("Blue") : Colors.Transparent,
            StrokeThickness = seg.IsActive ? 1 : 0,
            Content = row
        };

        if (seg.IsClickable)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SegmentClicked?.Invoke(seg);
            chip.GestureRecognizers.Add(tap);
        }

        return chip;
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
            "BlueSoft" => Color.FromArgb("#DBEAFE"),
            "Muted" => Color.FromArgb("#6B7280"),
            _ => Color.FromArgb("#111827")
        };
    }
}
