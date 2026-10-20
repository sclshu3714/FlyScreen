using System.Collections.Concurrent;
using FlyScreen.Helpers;
using FlyScreen.Services.Programs;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Media = System.Windows.Media;

namespace FlyScreen.Editor;

/// <summary>深色模式下提亮 AvalonEdit 语法色，避免关键字/方法几乎看不见。</summary>
internal static class EditorHighlightingTheme
{
    // 定义名 → (颜色名 → 原始前景 #AARRGGBB 或 #RRGGBB)
    private static readonly ConcurrentDictionary<string, Dictionary<string, string?>> OriginalForegrounds = new();

    public static void Apply(TextEditor editor, IHighlightingDefinition? definition)
    {
        if (editor == null) return;
        var dark = ThemeService.IsDark(ThemeService.Current);

        if (definition != null)
            AdaptDefinition(definition, dark);

        editor.SyntaxHighlighting = definition;

        // 默认正文色：高亮未覆盖的标识符也要可读
        if (ProgramEditorTheme.TryParseRgb(ProgramEditorTheme.EditorForegroundHex(dark), out var fr, out var fg, out var fb)
            && ProgramEditorTheme.TryParseRgb(ProgramEditorTheme.EditorBackgroundHex(dark), out var br, out var bg, out var bb))
        {
            editor.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(fr, fg, fb));
            editor.Background = new Media.SolidColorBrush(Media.Color.FromRgb(br, bg, bb));
        }

        var area = editor.TextArea;
        if (dark)
        {
            area.SelectionBrush = new Media.SolidColorBrush(Media.Color.FromArgb(0x66, 0x26, 0x4F, 0x78));
            area.SelectionForeground = new Media.SolidColorBrush(Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
            area.TextView.LinkTextForegroundBrush = new Media.SolidColorBrush(Media.Color.FromRgb(0x4F, 0xC1, 0xFF));
        }
        else
        {
            area.ClearValue(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty);
            area.ClearValue(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionForegroundProperty);
        }

        area.TextView.Redraw();
    }

    private static void AdaptDefinition(IHighlightingDefinition definition, bool dark)
    {
        var key = definition.Name ?? "default";
        var snap = OriginalForegrounds.GetOrAdd(key, _ =>
        {
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in definition.NamedHighlightingColors)
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                map[c.Name] = BrushToHex(c.Foreground);
            }
            return map;
        });

        foreach (var color in definition.NamedHighlightingColors)
        {
            if (string.IsNullOrEmpty(color.Name)) continue;
            if (dark)
                ApplyDark(color);
            else if (snap.TryGetValue(color.Name, out var hex) && hex != null
                     && ProgramEditorTheme.TryParseRgb(NormalizeRgb(hex), out var r, out var g, out var b))
                color.Foreground = new SimpleHighlightingBrush(Media.Color.FromRgb(r, g, b));
        }
    }

    private static void ApplyDark(HighlightingColor color)
    {
        var named = ProgramEditorTheme.DarkForegroundFor(color.Name);
        if (named != null && ProgramEditorTheme.TryParseRgb(named, out var r, out var g, out var b))
        {
            color.Foreground = new SimpleHighlightingBrush(Media.Color.FromRgb(r, g, b));
            return;
        }

        try
        {
            var existing = color.Foreground?.GetColor(null);
            if (existing is not Media.Color c) return;
            var hex = ProgramEditorTheme.EnsureReadableOnDark(c.R, c.G, c.B);
            if (ProgramEditorTheme.TryParseRgb(hex, out r, out g, out b))
                color.Foreground = new SimpleHighlightingBrush(Media.Color.FromRgb(r, g, b));
        }
        catch
        {
            /* ignore */
        }
    }

    private static string? BrushToHex(HighlightingBrush? brush)
    {
        try
        {
            var c = brush?.GetColor(null);
            return c is Media.Color x ? $"#{x.R:X2}{x.G:X2}{x.B:X2}" : null;
        }
        catch { return null; }
    }

    private static string NormalizeRgb(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 8) hex = hex[2..]; // AARRGGBB
        return "#" + hex;
    }
}
