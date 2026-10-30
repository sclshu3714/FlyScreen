using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.ScreenExtend;

/// <summary>GDI 抓取指定监视器区域（含软件绘制光标）并编码为 JPEG。</summary>
public static class ScreenCaptureJpeg
{
    public readonly record struct Frame(byte[] Jpeg, int Width, int Height);

    /// <param name="maxEdge">输出长边上限；≤0 表示不缩小（按监视器原始分辨率）。</param>
    public static Frame? Capture(MonitorRect monitor, long jpegQuality = 80L, int maxEdge = 1920)
    {
        var w = monitor.Width;
        var h = monitor.Height;
        if (w <= 0 || h <= 0) return null;

        var scale = 1.0;
        if (maxEdge > 0)
        {
            var longEdge = Math.Max(w, h);
            if (longEdge > maxEdge)
                scale = maxEdge / (double)longEdge;
        }

        var outW = Math.Max(1, (int)(w * scale));
        var outH = Math.Max(1, (int)(h * scale));

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bmp = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
        var old = NativeMethods.SelectObject(memDc, bmp);
        try
        {
            if (!NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, monitor.Left, monitor.Top, NativeMethods.SRCCOPY))
                return null;

            // BitBlt 不含光标；在帧上叠加当前指针（扩展屏硬件光标也不会进 GDI）
            TryDrawCursor(memDc, monitor);

            using var full = Image.FromHbitmap(bmp);
            using var ms = new MemoryStream();
            if (outW == w && outH == h)
            {
                SaveJpeg(full, ms, jpegQuality);
            }
            else
            {
                using var scaled = new Bitmap(outW, outH);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, outW, outH);
                }

                SaveJpeg(scaled, ms, jpegQuality);
            }

            return new Frame(ms.ToArray(), outW, outH);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, old);
            NativeMethods.DeleteObject(bmp);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void TryDrawCursor(IntPtr hdc, MonitorRect monitor)
    {
        try
        {
            var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref ci)) return;
            if ((ci.flags & NativeMethods.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;

            var x = ci.ptScreenPos.X;
            var y = ci.ptScreenPos.Y;
            if (x < monitor.Left || y < monitor.Top || x >= monitor.Right || y >= monitor.Bottom)
                return;

            var hx = 0;
            var hy = 0;
            if (NativeMethods.GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot;
                hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
            }

            NativeMethods.DrawIconEx(hdc, x - monitor.Left - hx, y - monitor.Top - hy, ci.hCursor,
                0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
        }
        catch
        {
            // ignore cursor overlay failures
        }
    }

    private static void SaveJpeg(Image image, Stream stream, long quality)
    {
        var enc = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid)
                  ?? throw new InvalidOperationException("JPEG encoder missing");
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(stream, enc, ep);
    }
}
