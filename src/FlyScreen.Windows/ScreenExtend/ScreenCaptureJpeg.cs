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
    public static Frame? Capture(MonitorRect monitor, long jpegQuality = 90L, int maxEdge = 0)
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

            // BitBlt 不含光标；按系统光标尺寸叠加（避免偏小/锯齿）
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
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
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
            var iconW = 0;
            var iconH = 0;
            if (NativeMethods.GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot;
                hy = ii.yHotspot;
                TryGetBitmapSize(ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask, out iconW, out iconH);
                // 掩码高度常为双倍
                if (ii.hbmColor == IntPtr.Zero && iconH > 0)
                    iconH /= 2;
                if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
            }

            var draw = ResolveCursorDrawSize(monitor, iconW);
            // 热区随绘制尺寸等比缩放
            var hxDraw = iconW > 0 ? (int)Math.Round(hx * (draw / (double)iconW)) : hx;
            var hyDraw = iconH > 0 ? (int)Math.Round(hy * (draw / (double)Math.Max(1, iconH))) : hy;

            NativeMethods.DrawIconEx(
                hdc,
                x - monitor.Left - hxDraw,
                y - monitor.Top - hyDraw,
                ci.hCursor,
                draw,
                draw,
                0,
                IntPtr.Zero,
                NativeMethods.DI_NORMAL);
        }
        catch
        {
            // ignore cursor overlay failures
        }
    }

    private static int ResolveCursorDrawSize(MonitorRect monitor, int iconW)
    {
        // 设置 → 辅助功能 → 鼠标指针大小（约 32–256）
        var slider = 0;
        try
        {
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETCURSORSIZE, 0, ref slider, 0)
                && slider >= 32)
                return Math.Clamp(slider, 32, 256);
        }
        catch { /* ignore */ }

        var sm = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXCURSOR);
        if (sm < 16) sm = 32;

        // 按副屏 DPI 放大（虚拟屏常为 96DPI，但系统指针在主屏可能更大）
        var dpi = TryGetMonitorDpi(monitor) ?? 96u;
        var dpiScaled = (int)Math.Round(sm * (dpi / 96.0));
        var preferred = Math.Max(sm, dpiScaled);

        // 若图标本身更大（高 DPI 资源），至少不小于图标宽
        if (iconW > preferred)
            preferred = iconW;

        return Math.Clamp(preferred, 32, 256);
    }

    private static uint? TryGetMonitorDpi(MonitorRect monitor)
    {
        try
        {
            var pt = new NativeMethods.POINT
            {
                X = monitor.Left + monitor.Width / 2,
                Y = monitor.Top + monitor.Height / 2
            };
            var hmon = MonitorFromPoint(pt, 2);
            if (hmon == IntPtr.Zero) return null;
            if (GetDpiForMonitor(hmon, 0, out var dx, out _) == 0 && dx > 0)
                return dx;
        }
        catch { /* ignore */ }

        return null;
    }

    private static void TryGetBitmapSize(IntPtr hbm, out int w, out int h)
    {
        w = 0;
        h = 0;
        if (hbm == IntPtr.Zero) return;
        try
        {
            var bm = new NativeMethods.BITMAP();
            if (NativeMethods.GetObject(hbm, Marshal.SizeOf<NativeMethods.BITMAP>(), ref bm) != 0)
            {
                w = bm.bmWidth;
                h = bm.bmHeight;
            }
        }
        catch { /* ignore */ }
    }

    private static void SaveJpeg(Image image, Stream stream, long quality)
    {
        var enc = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid)
                  ?? throw new InvalidOperationException("JPEG encoder missing");
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 50L, 100L));
        image.Save(stream, enc, ep);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
