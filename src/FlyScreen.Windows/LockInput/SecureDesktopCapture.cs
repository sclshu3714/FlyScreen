using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.LockInput;

/// <summary>
/// 在当前输入桌面（含 Winlogon/UAC）上 GDI 抓屏。
/// 须由 SYSTEM 会话助手线程调用（先 SetThreadDesktop）。
/// </summary>
public static class SecureDesktopCapture
{
    public readonly record struct Frame(byte[] Jpeg, int Width, int Height);

    /// <summary>抓取当前输入桌面的指定矩形；宽高无效时抓主屏全屏。</summary>
    public static Frame? CaptureJpeg(
        int left, int top, int width, int height,
        long jpegQuality = 85L, int maxEdge = 0, bool drawCursor = true)
    {
        return SecureDesktopInjector.RunOnInputDesktop((Func<Frame?>)(() =>
        {
            if (width <= 0 || height <= 0)
            {
                left = 0;
                top = 0;
                width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            }

            if (width <= 0 || height <= 0)
                return null;

            var scale = 1.0;
            if (maxEdge > 0)
            {
                var longEdge = Math.Max(width, height);
                if (longEdge > maxEdge)
                    scale = maxEdge / (double)longEdge;
            }

            var outW = Math.Max(1, (int)(width * scale));
            var outH = Math.Max(1, (int)(height * scale));

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var bmp = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            var old = NativeMethods.SelectObject(memDc, bmp);
            try
            {
                if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, left, top, NativeMethods.SRCCOPY))
                    return null;

                if (drawCursor)
                    TryDrawCursor(memDc, left, top, width, height);

                using var full = Image.FromHbitmap(bmp);
                using var ms = new MemoryStream();
                if (outW == width && outH == height)
                    SaveJpeg(full, ms, jpegQuality);
                else
                {
                    using var scaled = new Bitmap(outW, outH);
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
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
        }));
    }

    public static string? GetInputDesktopName()
        => SecureDesktopInjector.RunOnInputDesktop(SecureDesktopInjector.QueryCurrentDesktopName);

    private static void TryDrawCursor(IntPtr hdc, int left, int top, int width, int height)
    {
        try
        {
            var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref ci)) return;
            if ((ci.flags & NativeMethods.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;

            var x = ci.ptScreenPos.X;
            var y = ci.ptScreenPos.Y;
            if (x < left || y < top || x >= left + width || y >= top + height)
                return;

            NativeMethods.DrawIconEx(
                hdc,
                x - left,
                y - top,
                ci.hCursor,
                0,
                0,
                0,
                IntPtr.Zero,
                NativeMethods.DI_NORMAL);
        }
        catch
        {
            // ignore
        }
    }

    private static void SaveJpeg(Image image, Stream stream, long quality)
    {
        var enc = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid)
                  ?? throw new InvalidOperationException("JPEG encoder missing");
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 40L, 100L));
        image.Save(stream, enc, ep);
    }
}
