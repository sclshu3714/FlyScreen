using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace FlyScreen.Tool.VideoWall.Ui.Desktop;

/// <summary>多显示器工作区定位（画中画默认落在最左屏右侧垂直居中）。</summary>
internal static class VideoWallDisplayPlacement
{
    private const double DefaultPipWidth = 420;
    private const double DefaultPipHeight = 260;
    private const double EdgeMargin = 16;

    public static (double Width, double Height) DefaultPipSize => (DefaultPipWidth, DefaultPipHeight);

    public static PixelPoint DefaultPipTopLeft(Visual? visual)
    {
        var (w, h) = DefaultPipSize;
        try
        {
            var screens = TopLevel.GetTopLevel(visual)?.Screens?.All;
            if (screens is { Count: > 0 })
            {
                var left = screens.OrderBy(s => s.WorkingArea.X).First();
                var scale = left.Scaling <= 0 ? 1 : left.Scaling;
                var wa = left.WorkingArea;
                return new PixelPoint(
                    wa.Right - (int)((w + EdgeMargin) * scale),
                    wa.Y + Math.Max((int)(EdgeMargin * scale), (int)((wa.Height - h * scale) / 2)));
            }
        }
        catch { /* 取不到屏幕时回落 */ }

        return new PixelPoint(80, 80);
    }
}
