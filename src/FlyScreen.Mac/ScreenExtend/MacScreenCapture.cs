using System.Runtime.InteropServices;
using FlyScreen.Mac.Native;
using FlyScreen.Mac.Permissions;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Mac.ScreenExtend;

internal static class MacScreenCapture
{
    public readonly record struct Frame(byte[] Jpeg, int Width, int Height);

    public static Frame? Capture(uint displayId, int jpegQuality, int maxEdge, bool drawCursor)
    {
        if (!AccessibilityPermission.IsMacOS || displayId == 0) return null;
        IntPtr image = IntPtr.Zero;
        IntPtr rendered = IntPtr.Zero;
        try
        {
            image = MacDisplayNative.CGDisplayCreateImage(displayId);
            if (image == IntPtr.Zero) return null;

            var srcW = (int)MacDisplayNative.CGImageGetWidth(image);
            var srcH = (int)MacDisplayNative.CGImageGetHeight(image);
            if (srcW <= 0 || srcH <= 0) return null;

            var (outW, outH) = Fit(srcW, srcH, maxEdge);
            var encode = image;
            if (outW != srcW || outH != srcH || drawCursor)
            {
                double cx = 0, cy = 0;
                var hasCursor = drawCursor && TryCursorInImage(displayId, srcW, srcH, outW, outH, out cx, out cy);
                rendered = Render(image, outW, outH, hasCursor, cx, cy);
                if (rendered == IntPtr.Zero) return null;
                encode = rendered;
            }

            var quality = jpegQuality <= 0 ? 1.0 : Math.Clamp(jpegQuality, 1, 100) / 100.0;
            var jpeg = EncodeJpeg(encode, quality);
            return jpeg == null ? null : new Frame(jpeg, outW, outH);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (rendered != IntPtr.Zero) MacDisplayNative.CGImageRelease(rendered);
            if (image != IntPtr.Zero) MacDisplayNative.CGImageRelease(image);
        }
    }

    private static (int W, int H) Fit(int w, int h, int maxEdge)
    {
        if (maxEdge <= 0) return (w, h);
        var longEdge = Math.Max(w, h);
        if (longEdge <= maxEdge) return (w, h);
        var scale = maxEdge / (double)longEdge;
        return (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }

    private static bool TryCursorInImage(uint displayId, int srcW, int srcH, int outW, int outH, out double x, out double y)
    {
        x = y = 0;
        try
        {
            using var probe = new CursorEvent(CgEventNative.CGEventCreate(IntPtr.Zero));
            if (probe.Ptr == IntPtr.Zero) return false;
            var loc = CgEventNative.CGEventGetLocation(probe.Ptr);
            var bounds = MacDisplayNative.CGDisplayBounds(displayId);
            if (bounds.Size.Width < 1 || bounds.Size.Height < 1) return false;
            if (loc.X < bounds.Origin.X || loc.Y < bounds.Origin.Y
                || loc.X >= bounds.Origin.X + bounds.Size.Width
                || loc.Y >= bounds.Origin.Y + bounds.Size.Height)
                return false;

            var nx = (loc.X - bounds.Origin.X) / bounds.Size.Width;
            var ny = (bounds.Origin.Y + bounds.Size.Height - loc.Y) / bounds.Size.Height;
            x = nx * outW;
            y = ny * outH;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr Render(IntPtr source, int width, int height, bool cursor, double cx, double cy)
    {
        var space = MacDisplayNative.CGColorSpaceCreateDeviceRGB();
        if (space == IntPtr.Zero) return IntPtr.Zero;
        var info = MacDisplayNative.kCGBitmapByteOrder32Little | MacDisplayNative.kCGImageAlphaPremultipliedFirst;
        var ctx = MacDisplayNative.CGBitmapContextCreate(
            IntPtr.Zero, (nuint)width, (nuint)height, 8, (nuint)(width * 4), space, info);
        MacDisplayNative.CGColorSpaceRelease(space);
        if (ctx == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            MacDisplayNative.CGContextTranslateCTM(ctx, 0, height);
            MacDisplayNative.CGContextScaleCTM(ctx, 1, -1);
            MacDisplayNative.CGContextDrawImage(ctx, new MacDisplayNative.CGRect
            {
                Origin = new MacDisplayNative.CGPoint(),
                Size = new MacDisplayNative.CGSize { Width = width, Height = height }
            }, source);
            if (cursor)
            {
                const double r = 7;
                MacDisplayNative.CGContextSetRGBFillColor(ctx, 0, 0, 0, 0.9);
                MacDisplayNative.CGContextFillEllipseInRect(ctx, Ellipse(cx, cy, r + 2));
                MacDisplayNative.CGContextSetRGBFillColor(ctx, 1, 1, 1, 1);
                MacDisplayNative.CGContextFillEllipseInRect(ctx, Ellipse(cx, cy, r));
            }

            return MacDisplayNative.CGBitmapContextCreateImage(ctx);
        }
        finally
        {
            MacDisplayNative.CGContextRelease(ctx);
        }
    }

    private static MacDisplayNative.CGRect Ellipse(double cx, double cy, double r) => new()
    {
        Origin = new MacDisplayNative.CGPoint { X = cx - r, Y = cy - r },
        Size = new MacDisplayNative.CGSize { Width = r * 2, Height = r * 2 }
    };

    private static byte[]? EncodeJpeg(IntPtr image, double quality)
    {
        var type = MacDisplayNative.CFStringCreateWithCString(IntPtr.Zero, "public.jpeg", MacDisplayNative.kCFStringEncodingUTF8);
        var key = MacDisplayNative.CFStringCreateWithCString(
            IntPtr.Zero, "kCGImageDestinationLossyCompressionQuality", MacDisplayNative.kCFStringEncodingUTF8);
        var q = quality;
        var number = MacDisplayNative.CFNumberCreate(IntPtr.Zero, MacDisplayNative.kCFNumberFloat64Type, ref q);
        var data = MacDisplayNative.CFDataCreateMutable(IntPtr.Zero, 0);
        IntPtr dest = IntPtr.Zero;
        IntPtr props = IntPtr.Zero;
        try
        {
            if (type == IntPtr.Zero || data == IntPtr.Zero) return null;
            if (key != IntPtr.Zero && number != IntPtr.Zero)
            {
                props = MacDisplayNative.CFDictionaryCreate(
                    IntPtr.Zero, [key], [number], 1, IntPtr.Zero, IntPtr.Zero);
            }

            dest = MacDisplayNative.CGImageDestinationCreateWithData(data, type, 1, IntPtr.Zero);
            if (dest == IntPtr.Zero) return null;
            MacDisplayNative.CGImageDestinationAddImage(dest, image, props);
            if (!MacDisplayNative.CGImageDestinationFinalize(dest)) return null;
            var len = MacDisplayNative.CFDataGetLength(data);
            if (len <= 0) return null;
            var ptr = MacDisplayNative.CFDataGetBytePtr(data);
            if (ptr == IntPtr.Zero) return null;
            var jpeg = new byte[len];
            Marshal.Copy(ptr, jpeg, 0, (int)len);
            return jpeg;
        }
        finally
        {
            if (dest != IntPtr.Zero) MacDisplayNative.CFRelease(dest);
            if (props != IntPtr.Zero) MacDisplayNative.CFRelease(props);
            if (number != IntPtr.Zero) MacDisplayNative.CFRelease(number);
            if (key != IntPtr.Zero) MacDisplayNative.CFRelease(key);
            if (type != IntPtr.Zero) MacDisplayNative.CFRelease(type);
            if (data != IntPtr.Zero) MacDisplayNative.CFRelease(data);
        }
    }

    private sealed class CursorEvent : IDisposable
    {
        public IntPtr Ptr { get; }
        public CursorEvent(IntPtr ptr) => Ptr = ptr;
        public void Dispose()
        {
            if (Ptr != IntPtr.Zero)
            {
                try { CgEventNative.CFRelease(Ptr); } catch { /* ignore */ }
            }
        }
    }
}
