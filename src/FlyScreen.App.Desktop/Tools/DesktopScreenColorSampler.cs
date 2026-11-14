using System.Drawing;
using System.Runtime.Versioning;
using FlyScreen.ToolSdk;

namespace FlyScreen.DesktopUi.Tools;

/// <summary>桌面端屏幕采样（GDI CopyFromScreen；Mac/Linux 依赖系统图形库）。</summary>
public sealed class DesktopScreenColorSampler : IScreenColorSampler
{
#pragma warning disable CA1416
    public bool TrySample(int screenX, int screenY, out RgbaColor color)
    {
        try
        {
            using var bmp = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screenX, screenY, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy);
            }

            var p = bmp.GetPixel(0, 0);
            color = new RgbaColor(p.R, p.G, p.B, p.A);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }
#pragma warning restore CA1416
}
