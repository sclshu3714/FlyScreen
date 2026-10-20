namespace FlyScreen.Services.Programs;

/// <summary>编辑器深/浅色：Monaco 主题 id 与语法高亮建议色（#RRGGBB）。</summary>
public static class ProgramEditorTheme
{
    public static string MonacoThemeId(bool dark) => dark ? "vs-dark" : "vs";

    public static string EditorBackgroundHex(bool dark) => dark ? "#0F172A" : "#FFFFFF";

    public static string EditorForegroundHex(bool dark) => dark ? "#E2E8F0" : "#111827";

    /// <summary>断点栏 / 行号旁 gutter 背景。</summary>
    public static string GutterBackgroundHex(bool dark) => dark ? "#1E293B" : "#F3F4F6";

    /// <summary>按 AvalonEdit NamedHighlightingColor.Name 给出深色前景；无法识别则返回 null。</summary>
    public static string? DarkForegroundFor(string? colorName)
    {
        if (string.IsNullOrEmpty(colorName)) return null;
        var n = colorName.ToLowerInvariant();

        if (n.Contains("comment") || n.Contains("doccomment") || n.Contains("xml doc"))
            return "#6A9955";
        if (n.Contains("string") || n.Contains("character") || n.Contains("char"))
            return "#CE9178";
        if (n.Contains("number") || n.Contains("digit") || n.Contains("literal"))
            return "#B5CEA8";
        if (n.Contains("preprocessor") || n.Contains("directive"))
            return "#C586C0";
        if (n.Contains("keyword") || n.Contains("reserved") || n is "this" or "value" or "true" or "false" or "null")
            return "#569CD6";
        if (n.Contains("type") || n.Contains("class") || n.Contains("struct") || n.Contains("enum")
            || n.Contains("interface") || n.Contains("delegate") || n.Contains("record"))
            return "#4EC9B0";
        if (n.Contains("method") || n.Contains("function") || n.Contains("definition") || n.Contains("member"))
            return "#DCDCAA";
        if (n.Contains("parameter") || n.Contains("field") || n.Contains("variable") || n.Contains("identifier")
            || n.Contains("name"))
            return "#9CDCFE";
        if (n.Contains("namespace") || n.Contains("using"))
            return "#4EC9B0";
        if (n.Contains("attribute") || n.Contains("annotation"))
            return "#9CDCFE";
        if (n.Contains("operator") || n.Contains("punctuation") || n.Contains("symbol"))
            return "#D4D4D4";
        if (n.Contains("regex") || n.Contains("escape"))
            return "#D7BA7D";
        if (n.Contains("tag") || n.Contains("element"))
            return "#569CD6";
        if (n.Contains("attribute name") || n.Contains("attr"))
            return "#9CDCFE";
        if (n.Contains("error") || n.Contains("warning"))
            return "#F44747";
        if (n.Contains("constant") || n.Contains("enum member"))
            return "#4FC1FF";
        return null;
    }

    /// <summary>若颜色过暗（相对深色背景），提亮到可读亮度。</summary>
    public static string EnsureReadableOnDark(byte r, byte g, byte b)
    {
        // 相对亮度（简化）
        var lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        if (lum >= 0.45) return $"#{r:X2}{g:X2}{b:X2}";

        // 向白色混合，保证对比
        const double t = 0.55;
        var nr = (byte)Math.Clamp((int)(r + (255 - r) * t), 0, 255);
        var ng = (byte)Math.Clamp((int)(g + (255 - g) * t), 0, 255);
        var nb = (byte)Math.Clamp((int)(b + (255 - b) * t), 0, 255);
        // 再保底一次
        lum = (0.2126 * nr + 0.7152 * ng + 0.0722 * nb) / 255.0;
        if (lum < 0.55)
        {
            nr = (byte)Math.Clamp(nr + 40, 0, 255);
            ng = (byte)Math.Clamp(ng + 40, 0, 255);
            nb = (byte)Math.Clamp(nb + 40, 0, 255);
        }
        return $"#{nr:X2}{ng:X2}{nb:X2}";
    }

    public static bool TryParseRgb(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 8) hex = hex[2..]; // AARRGGBB → RRGGBB
        if (hex.Length != 6) return false;
        return byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
               && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
               && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }
}
