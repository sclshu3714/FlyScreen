using System.Globalization;

namespace FlyScreen.Services.Tools.ColorPicker;

/// <summary>将 <see cref="RgbaColor"/> 转为常用开发/设计格式。</summary>
public static class ColorFormatConverter
{
    public static IReadOnlyList<ColorFormatEntry> ConvertAll(RgbaColor c)
    {
        var r = c.R;
        var g = c.G;
        var b = c.B;
        var a = c.A;
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var af = a / 255.0;

        RgbToHsl(rf, gf, bf, out var h, out var s, out var l);
        RgbToHsv(rf, gf, bf, out var hv, out var sv, out var vv);
        RgbToHwb(rf, gf, bf, out var hw, out var ww, out var bw);
        RgbToCmyk(rf, gf, bf, out var cy, out var m, out var y, out var k);
        RgbToXyz(rf, gf, bf, out var x, out var yy, out var z);
        XyzToLab(x, yy, z, out var labL, out var labA, out var labB);
        LabToLch(labL, labA, labB, out var lchL, out var lchC, out var lchH);

        var hex = $"#{r:X2}{g:X2}{b:X2}";
        var hexA = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        var android = unchecked((uint)((a << 24) | (r << 16) | (g << 8) | b));

        return
        [
            new ColorFormatEntry { Key = "hex", Label = "HEX", Value = hex },
            new ColorFormatEntry { Key = "hexa", Label = "HEX (AARRGGBB)", Value = hexA },
            new ColorFormatEntry { Key = "rgb", Label = "RGB", Value = $"rgb({r}, {g}, {b})" },
            new ColorFormatEntry
            {
                Key = "rgba",
                Label = "RGBA",
                Value = string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.###})", r, g, b, af)
            },
            new ColorFormatEntry
            {
                Key = "hsl",
                Label = "HSL",
                Value = string.Format(CultureInfo.InvariantCulture, "hsl({0:0.#}, {1:0.#}%, {2:0.#}%)", h, s * 100, l * 100)
            },
            new ColorFormatEntry
            {
                Key = "hsla",
                Label = "HSLA",
                Value = string.Format(CultureInfo.InvariantCulture,
                    "hsla({0:0.#}, {1:0.#}%, {2:0.#}%, {3:0.###})", h, s * 100, l * 100, af)
            },
            new ColorFormatEntry
            {
                Key = "hsv",
                Label = "HSV / HSB",
                Value = string.Format(CultureInfo.InvariantCulture, "hsv({0:0.#}, {1:0.#}%, {2:0.#}%)", hv, sv * 100, vv * 100)
            },
            new ColorFormatEntry
            {
                Key = "hwb",
                Label = "HWB",
                Value = string.Format(CultureInfo.InvariantCulture, "hwb({0:0.#} {1:0.#}% {2:0.#}%)", hw, ww * 100, bw * 100)
            },
            new ColorFormatEntry
            {
                Key = "cmyk",
                Label = "CMYK",
                Value = string.Format(CultureInfo.InvariantCulture,
                    "cmyk({0:0.#}%, {1:0.#}%, {2:0.#}%, {3:0.#}%)", cy * 100, m * 100, y * 100, k * 100)
            },
            new ColorFormatEntry
            {
                Key = "lab",
                Label = "LAB",
                Value = string.Format(CultureInfo.InvariantCulture, "lab({0:0.##} {1:0.##} {2:0.##})", labL, labA, labB)
            },
            new ColorFormatEntry
            {
                Key = "lch",
                Label = "LCH",
                Value = string.Format(CultureInfo.InvariantCulture, "lch({0:0.##} {1:0.##} {2:0.#})", lchL, lchC, lchH)
            },
            new ColorFormatEntry
            {
                Key = "xyz",
                Label = "XYZ",
                Value = string.Format(CultureInfo.InvariantCulture, "xyz({0:0.####}, {1:0.####}, {2:0.####})", x, yy, z)
            },
            new ColorFormatEntry
            {
                Key = "css-rgb",
                Label = "CSS rgb()",
                Value = string.Format(CultureInfo.InvariantCulture, "rgb({0} {1} {2} / {3:0.###})", r, g, b, af)
            },
            new ColorFormatEntry
            {
                Key = "css-hsl",
                Label = "CSS hsl()",
                Value = string.Format(CultureInfo.InvariantCulture,
                    "hsl({0:0.#} {1:0.#}% {2:0.#}% / {3:0.###})", h, s * 100, l * 100, af)
            },
            new ColorFormatEntry
            {
                Key = "android",
                Label = "Android Color",
                Value = "0x" + android.ToString("X8", CultureInfo.InvariantCulture)
            },
            new ColorFormatEntry
            {
                Key = "flutter",
                Label = "Flutter Color",
                Value = "Color(0x" + android.ToString("X8", CultureInfo.InvariantCulture) + ")"
            },
            new ColorFormatEntry
            {
                Key = "swift",
                Label = "Swift UIColor",
                Value = string.Format(CultureInfo.InvariantCulture,
                    "UIColor(red: {0:0.###}, green: {1:0.###}, blue: {2:0.###}, alpha: {3:0.###})",
                    rf, gf, bf, af)
            },
            new ColorFormatEntry
            {
                Key = "decimal",
                Label = "Decimal RGB",
                Value = ((r << 16) | (g << 8) | b).ToString(CultureInfo.InvariantCulture)
            }
        ];
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2.0;
        if (Math.Abs(max - min) < 1e-9)
        {
            h = 0;
            s = 0;
            return;
        }

        var d = max - min;
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        if (Math.Abs(max - r) < 1e-9)
            h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
        else if (Math.Abs(max - g) < 1e-9)
            h = ((b - r) / d + 2) * 60;
        else
            h = ((r - g) / d + 4) * 60;
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        v = max;
        var d = max - min;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0)
        {
            h = 0;
            return;
        }

        if (Math.Abs(max - r) < 1e-9)
            h = 60 * (((g - b) / d) % 6);
        else if (Math.Abs(max - g) < 1e-9)
            h = 60 * ((b - r) / d + 2);
        else
            h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    private static void RgbToHwb(double r, double g, double b, out double h, out double w, out double bl)
    {
        RgbToHsv(r, g, b, out h, out _, out _);
        w = Math.Min(r, Math.Min(g, b));
        bl = 1.0 - Math.Max(r, Math.Max(g, b));
    }

    private static void RgbToCmyk(double r, double g, double b, out double c, out double m, out double y, out double k)
    {
        k = 1.0 - Math.Max(r, Math.Max(g, b));
        if (k >= 1.0 - 1e-9)
        {
            c = m = y = 0;
            k = 1;
            return;
        }

        c = (1 - r - k) / (1 - k);
        m = (1 - g - k) / (1 - k);
        y = (1 - b - k) / (1 - k);
    }

    private static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static void RgbToXyz(double r, double g, double b, out double x, out double y, out double z)
    {
        r = SrgbToLinear(r);
        g = SrgbToLinear(g);
        b = SrgbToLinear(b);
        x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
        y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;
    }

    private static double PivotXyz(double t)
    {
        const double e = 216.0 / 24389.0;
        const double k = 24389.0 / 27.0;
        return t > e ? Math.Pow(t, 1.0 / 3.0) : (k * t + 16.0) / 116.0;
    }

    private static void XyzToLab(double x, double y, double z, out double l, out double a, out double b)
    {
        // D65
        x = PivotXyz(x / 0.95047);
        y = PivotXyz(y / 1.00000);
        z = PivotXyz(z / 1.08883);
        l = 116 * y - 16;
        a = 500 * (x - y);
        b = 200 * (y - z);
    }

    private static void LabToLch(double l, double a, double b, out double L, out double C, out double H)
    {
        L = l;
        C = Math.Sqrt(a * a + b * b);
        H = Math.Atan2(b, a) * 180.0 / Math.PI;
        if (H < 0) H += 360;
    }
}
