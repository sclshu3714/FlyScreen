using System.Text.Json.Serialization;

namespace FlyScreen.Services.Programs;

/// <summary>设置字段控件类型（由 @fly-settings 标注解析）。</summary>
public enum ProgramSettingKind
{
    Bool,
    StringList,
    Enum,
    Double,
    Rect
}

/// <summary>命中识别后在操作区执行的动作。</summary>
public enum ProgramHitAction
{
    SwipeUp,
    SwipeDown,
    SwipeLeft,
    SwipeRight,
    Tap,
    DoubleTap,
    LongPress
}

/// <summary>归一化矩形（相对全屏 0..1，左上原点）。</summary>
public readonly struct NormRect
{
    public NormRect(double x, double y, double w, double h)
    {
        X = Clamp01(x);
        Y = Clamp01(y);
        W = Math.Clamp(w, 0.02, 1);
        H = Math.Clamp(h, 0.02, 1);
        if (X + W > 1) W = Math.Max(0.02, 1 - X);
        if (Y + H > 1) H = Math.Max(0.02, 1 - Y);
    }

    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }

    public bool IsEmpty => W <= 0 || H <= 0;

    public (int L, int T, int R, int B) ToPixels(int screenW, int screenH)
    {
        var sw = Math.Max(screenW, 1);
        var sh = Math.Max(screenH, 1);
        var l = (int)Math.Floor(X * sw);
        var t = (int)Math.Floor(Y * sh);
        var r = (int)Math.Ceiling((X + W) * sw);
        var b = (int)Math.Ceiling((Y + H) * sh);
        l = Math.Clamp(l, 0, sw);
        t = Math.Clamp(t, 0, sh);
        r = Math.Clamp(r, 0, sw);
        b = Math.Clamp(b, 0, sh);
        if (r <= l) r = Math.Min(sw, l + 1);
        if (b <= t) b = Math.Min(sh, t + 1);
        return (l, t, r, b);
    }

    public static NormRect FromPixels(int l, int t, int r, int b, int screenW, int screenH)
    {
        var sw = Math.Max(screenW, 1);
        var sh = Math.Max(screenH, 1);
        var left = Math.Min(l, r);
        var top = Math.Min(t, b);
        var right = Math.Max(l, r);
        var bottom = Math.Max(t, b);
        return new NormRect(
            left / (double)sw,
            top / (double)sh,
            (right - left) / (double)sw,
            (bottom - top) / (double)sh);
    }

    public static NormRect? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split([',', ' ', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4) return null;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x)) return null;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var y)) return null;
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var w)) return null;
        if (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var h)) return null;
        return new NormRect(x, y, w, h);
    }

    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{X:0.####},{Y:0.####},{W:0.####},{H:0.####}");

    /// <summary>UI 展示用短串，不改变存储精度。</summary>
    public string ToDisplayString(int decimals = 3)
    {
        decimals = Math.Clamp(decimals, 0, 6);
        var fmt = "0." + new string('0', decimals);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Create(inv, $"{X.ToString(fmt, inv)},{Y.ToString(fmt, inv)},{W.ToString(fmt, inv)},{H.ToString(fmt, inv)}");
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}

/// <summary>脚本 @fly-settings 中声明的一项。</summary>
public sealed class ProgramSettingField
{
    public required string Key { get; init; }
    public required ProgramSettingKind Kind { get; init; }
    public required string Label { get; init; }
    public string DefaultRaw { get; init; } = "";
    /// <summary>enum 可选值（原始 token）。</summary>
    public IReadOnlyList<string> EnumOptions { get; init; } = [];
}

/// <summary>落盘 settings.json。</summary>
public sealed class ProgramSettingsFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>内置通用程序常量。</summary>
public static class BuiltinPrograms
{
    public const string GenericSwipeId = "builtin-generic-swipe";
    public const int GenericSwipeSourceVersion = 8;
    public const string GenericSwipeName = "通用识别操作";
    public const string ResourceName = "FlyScreen.Services.Programs.Builtin.generic_swipe_main.cs";
}
