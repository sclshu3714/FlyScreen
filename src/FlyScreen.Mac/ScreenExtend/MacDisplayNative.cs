using System.Runtime.InteropServices;

namespace FlyScreen.Mac.ScreenExtend;

/// <summary>CoreGraphics / ImageIO / CoreFoundation。仅在 macOS 上调用。</summary>
internal static class MacDisplayNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIo = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint kCGImageAlphaPremultipliedFirst = 2;
    public const uint kCGBitmapByteOrder32Little = 2 << 12;
    public const int kCGConfigureForSession = 1;
    public const int kCFStringEncodingUTF8 = 0x08000100;
    public const int kCFNumberFloat64Type = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    [DllImport(CoreGraphics)]
    public static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    public static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    public static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    public static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(CoreGraphics)]
    public static extern nuint CGDisplayPixelsHigh(uint display);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGDisplayCreateImage(uint display);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetWidth(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetHeight(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern void CGImageRelease(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    public static extern void CGColorSpaceRelease(IntPtr space);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGBitmapContextCreate(
        IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow,
        IntPtr space, uint bitmapInfo);

    [DllImport(CoreGraphics)]
    public static extern void CGContextRelease(IntPtr c);

    [DllImport(CoreGraphics)]
    public static extern void CGContextDrawImage(IntPtr c, CGRect rect, IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern void CGContextTranslateCTM(IntPtr c, double tx, double ty);

    [DllImport(CoreGraphics)]
    public static extern void CGContextScaleCTM(IntPtr c, double sx, double sy);

    [DllImport(CoreGraphics)]
    public static extern void CGContextSetRGBFillColor(IntPtr c, double r, double g, double b, double a);

    [DllImport(CoreGraphics)]
    public static extern void CGContextFillEllipseInRect(IntPtr c, CGRect rect);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGBitmapContextCreateImage(IntPtr c);

    [DllImport(CoreGraphics)]
    public static extern int CGBeginDisplayConfiguration(out IntPtr config);

    [DllImport(CoreGraphics)]
    public static extern int CGConfigureDisplayOrigin(IntPtr config, uint display, int x, int y);

    [DllImport(CoreGraphics)]
    public static extern int CGCompleteDisplayConfiguration(IntPtr config, int option);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestScreenCaptureAccess();

    [DllImport(ImageIo)]
    public static extern IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, nuint count, IntPtr options);

    [DllImport(ImageIo)]
    public static extern void CGImageDestinationAddImage(IntPtr dest, IntPtr image, IntPtr properties);

    [DllImport(ImageIo)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGImageDestinationFinalize(IntPtr dest);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);

    [DllImport(CoreFoundation)]
    public static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, int encoding);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFNumberCreate(IntPtr allocator, int theType, ref double value);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFRetain(IntPtr cf);

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);
}
