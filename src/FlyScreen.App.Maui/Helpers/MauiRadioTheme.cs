namespace FlyScreen.AppMaui.Helpers;

/// <summary>
/// RadioButton 主题字色：WinUI 对字符串 Content + TextColor 经常不刷新；
/// 统一用 Label 承载文案，并在 Windows 上刷原生 TextBlock.Foreground。
/// </summary>
public static class MauiRadioTheme
{
    public static void SetContent(RadioButton rb, string text)
    {
        var color = TextColor();
        if (rb.Content is Label lbl)
        {
            lbl.Text = text;
            lbl.TextColor = color;
            lbl.FontSize = rb.FontSize > 0 ? rb.FontSize : 13;
        }
        else
        {
            rb.Content = new Label
            {
                Text = text,
                TextColor = color,
                FontSize = rb.FontSize > 0 ? rb.FontSize : 13,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.NoWrap
            };
        }

        rb.TextColor = color;
        rb.BackgroundColor = Color.FromArgb("#01000000");
        ForcePlatformForeground(rb, color);
    }

    public static void RefreshAll(Element? root)
    {
        if (root == null) return;
        var color = TextColor();
        foreach (var rb in Enumerate(root).OfType<RadioButton>())
        {
            if (rb.Content is Label lbl)
            {
                lbl.TextColor = color;
                rb.TextColor = color;
            }
            else if (rb.Content is string s)
            {
                SetContent(rb, s);
                continue;
            }
            else
            {
                rb.TextColor = color;
            }

            rb.BackgroundColor = Color.FromArgb("#01000000");
            ForcePlatformForeground(rb, color);
            try { rb.Handler?.UpdateValue(nameof(RadioButton.TextColor)); } catch { /* ignore */ }
        }
    }

    private static Color TextColor()
    {
        if (Application.Current?.Resources.TryGetValue("Text", out var v) == true && v is Color c)
            return c;
        return MauiThemeService.IsDark(MauiThemeService.Current)
            ? Color.FromArgb("#E2E8F0")
            : Color.FromArgb("#111827");
    }

    private static void ForcePlatformForeground(RadioButton rb, Color mauiColor)
    {
#if WINDOWS
        try
        {
            if (rb.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.RadioButton native)
                return;

            var win = global::Windows.UI.Color.FromArgb(
                (byte)Math.Clamp((int)(mauiColor.Alpha * 255), 0, 255),
                (byte)Math.Clamp((int)(mauiColor.Red * 255), 0, 255),
                (byte)Math.Clamp((int)(mauiColor.Green * 255), 0, 255),
                (byte)Math.Clamp((int)(mauiColor.Blue * 255), 0, 255));
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(win);
            // 覆盖主题资源前景，再递归刷 Content 内 TextBlock（WinUI Radio 字色多在子级）
            native.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);
            native.Foreground = brush;
            PaintTextBlocks(native, brush);
        }
        catch
        {
            // ignore
        }
#endif
    }

#if WINDOWS
    private static void PaintTextBlocks(Microsoft.UI.Xaml.DependencyObject root, Microsoft.UI.Xaml.Media.Brush brush)
    {
        if (root is Microsoft.UI.Xaml.Controls.TextBlock tb)
            tb.Foreground = brush;

        var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            PaintTextBlocks(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), brush);
    }
#endif

    private static IEnumerable<Element> Enumerate(Element root)
    {
        yield return root;
        if (root is not IVisualTreeElement vte) yield break;
        foreach (var child in vte.GetVisualChildren())
        {
            if (child is not Element el) continue;
            foreach (var x in Enumerate(el))
                yield return x;
        }
    }
}
