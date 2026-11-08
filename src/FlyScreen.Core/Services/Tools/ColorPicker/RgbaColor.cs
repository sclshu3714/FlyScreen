namespace FlyScreen.Services.Tools.ColorPicker;

/// <summary>0–255 通道颜色（A 默认不透明）。</summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static RgbaColor FromRgb(int r, int g, int b)
        => new((byte)Clamp(r), (byte)Clamp(g), (byte)Clamp(b));

    public static RgbaColor FromArgb(int a, int r, int g, int b)
        => new((byte)Clamp(r), (byte)Clamp(g), (byte)Clamp(b), (byte)Clamp(a));

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
