using System.Collections.Concurrent;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi.Editor;

/// <summary>深色模式下提亮 AvaloniaEdit 语法色。</summary>
internal static class EditorHighlightingTheme
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, string?>> OriginalForegrounds = new();

    public static void Apply(TextEditor editor, IHighlightingDefinition? definition)
    {
        if (editor == null) return;
        var dark = DesktopThemeService.IsDark(DesktopThemeService.Current);

        if (definition != null)
            AdaptDefinition(definition, dark);

        editor.SyntaxHighlighting = definition;

        if (ProgramEditorTheme.TryParseRgb(ProgramEditorTheme.EditorForegroundHex(dark), out var fr, out var fg, out var fb)
            && ProgramEditorTheme.TryParseRgb(ProgramEditorTheme.EditorBackgroundHex(dark), out var br, out var bg, out var bb))
        {
            editor.Foreground = new SolidColorBrush(Color.FromRgb(fr, fg, fb));
            editor.Background = new SolidColorBrush(Color.FromRgb(br, bg, bb));
        }

        var area = editor.TextArea;
        if (dark)
        {
            area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x26, 0x4F, 0x78));
            area.SelectionForeground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
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
                color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(r, g, b));
        }
    }

    private static void ApplyDark(HighlightingColor color)
    {
        var named = ProgramEditorTheme.DarkForegroundFor(color.Name);
        if (named != null && ProgramEditorTheme.TryParseRgb(named, out var r, out var g, out var b))
        {
            color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(r, g, b));
            return;
        }

        try
        {
            var existing = color.Foreground?.GetColor(null);
            if (existing is not Color c) return;
            var hex = ProgramEditorTheme.EnsureReadableOnDark(c.R, c.G, c.B);
            if (ProgramEditorTheme.TryParseRgb(hex, out r, out g, out b))
                color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(r, g, b));
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
            return c is Color x ? $"#{x.R:X2}{x.G:X2}{x.B:X2}" : null;
        }
        catch { return null; }
    }

    private static string NormalizeRgb(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 8) hex = hex[2..];
        return "#" + hex;
    }
}
