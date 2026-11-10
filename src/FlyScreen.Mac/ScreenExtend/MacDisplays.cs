using FlyScreen.Mac.Permissions;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Mac.ScreenExtend;

public readonly record struct MacDisplayInfo(
    uint Id,
    bool IsPrimary,
    double X,
    double Y,
    double PointWidth,
    double PointHeight,
    int PixelWidth,
    int PixelHeight,
    int ScalePercent);

public static class MacDisplays
{
    public static bool IsMac => AccessibilityPermission.IsMacOS;

    public static IReadOnlyList<MacDisplayInfo> List()
    {
        if (!IsMac) return [];
        try
        {
            var ids = new uint[16];
            if (MacDisplayNative.CGGetActiveDisplayList((uint)ids.Length, ids, out var count) != 0 || count == 0)
                return [];
            var main = MacDisplayNative.CGMainDisplayID();
            var list = new List<MacDisplayInfo>((int)count);
            for (var i = 0; i < count; i++)
                list.Add(Describe(ids[i], ids[i] == main));
            return list;
        }
        catch
        {
            return [];
        }
    }

    public static MacDisplayInfo? TryPrimary()
    {
        foreach (var d in List())
        {
            if (d.IsPrimary) return d;
        }

        return List().Count > 0 ? List()[0] : null;
    }

    public static MacDisplayInfo? TrySecondary()
    {
        foreach (var d in List())
        {
            if (!d.IsPrimary && d.PixelWidth > 0 && d.PixelHeight > 0)
                return d;
        }

        return null;
    }

    public static bool HasSecondary() => TrySecondary() != null;

    public static DisplayInfoPayload ReadPrimaryPayload()
    {
        var d = TryPrimary();
        if (d == null)
        {
            return new DisplayInfoPayload
            {
                Width = 1920,
                Height = 1080,
                ScalePercent = 100,
                RefreshHz = 60
            };
        }

        return new DisplayInfoPayload
        {
            Width = d.Value.PixelWidth,
            Height = d.Value.PixelHeight,
            ScalePercent = d.Value.ScalePercent,
            RefreshHz = 60
        };
    }

    /// <summary>主屏点尺寸，供 CGEvent 坐标使用（不是像素）。</summary>
    public static void ReadPrimaryPoints(out double w, out double h)
    {
        w = 1920;
        h = 1080;
        var d = TryPrimary();
        if (d == null) return;
        if (d.Value.PointWidth > 1) w = d.Value.PointWidth;
        if (d.Value.PointHeight > 1) h = d.Value.PointHeight;
    }

    public static bool TryPlace(uint displayId, MacDisplayInfo anchor, int pixelWidth, int pixelHeight, LayoutEdgeSide side)
    {
        if (!IsMac || displayId == 0) return false;
        try
        {
            var placed = List().FirstOrDefault(d => d.Id == displayId);
            var pointW = placed.PointWidth > 1
                ? placed.PointWidth
                : Math.Max(1, pixelWidth);
            var pointH = placed.PointHeight > 1
                ? placed.PointHeight
                : Math.Max(1, pixelHeight);
            // CoreGraphics 全局坐标原点在左下，Y 向上。
            double x = anchor.X;
            double y = anchor.Y;
            switch (side)
            {
                case LayoutEdgeSide.Left:
                    x = anchor.X - pointW;
                    break;
                case LayoutEdgeSide.Top:
                    y = anchor.Y + anchor.PointHeight;
                    break;
                case LayoutEdgeSide.Bottom:
                    y = anchor.Y - pointH;
                    break;
                default:
                    x = anchor.X + anchor.PointWidth;
                    break;
            }

            if (MacDisplayNative.CGBeginDisplayConfiguration(out var config) != 0 || config == IntPtr.Zero)
                return false;
            try
            {
                MacDisplayNative.CGConfigureDisplayOrigin(config, displayId, (int)Math.Round(x), (int)Math.Round(y));
                return MacDisplayNative.CGCompleteDisplayConfiguration(config, MacDisplayNative.kCGConfigureForSession) == 0;
            }
            catch
            {
                try { MacDisplayNative.CGCompleteDisplayConfiguration(config, MacDisplayNative.kCGConfigureForSession); }
                catch { /* ignore */ }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static MacDisplayInfo Describe(uint id, bool primary)
    {
        var bounds = MacDisplayNative.CGDisplayBounds(id);
        var pw = (int)MacDisplayNative.CGDisplayPixelsWide(id);
        var ph = (int)MacDisplayNative.CGDisplayPixelsHigh(id);
        var scale = 100;
        if (bounds.Size.Width > 1 && pw > 0)
            scale = (int)Math.Clamp(Math.Round(pw / bounds.Size.Width * 100), 100, 300);
        return new MacDisplayInfo(
            id,
            primary,
            bounds.Origin.X,
            bounds.Origin.Y,
            bounds.Size.Width,
            bounds.Size.Height,
            Math.Max(1, pw),
            Math.Max(1, ph),
            scale);
    }
}
