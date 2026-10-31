using System.Runtime.InteropServices;

namespace FlyScreen.Mac.Native;

/// <summary>
/// CoreGraphics / ApplicationServices P/Invoke。仅在 macOS 上加载；Windows 编译可通过。
/// </summary>
internal static class CgEventNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    public const uint kCGEventMouseMoved = 5;
    public const uint kCGEventLeftMouseDown = 1;
    public const uint kCGEventLeftMouseUp = 2;
    public const uint kCGEventRightMouseDown = 3;
    public const uint kCGEventRightMouseUp = 4;
    public const uint kCGEventOtherMouseDown = 25;
    public const uint kCGEventOtherMouseUp = 26;
    public const uint kCGEventScrollWheel = 22;
    public const uint kCGEventKeyDown = 10;
    public const uint kCGEventKeyUp = 11;

    public const uint kCGHIDEventTap = 0;
    public const uint kCGMouseButtonLeft = 0;
    public const uint kCGMouseButtonRight = 1;
    public const uint kCGMouseButtonCenter = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;
    }

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint position, uint mouseButton);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    public static extern void CGEventKeyboardSetUnicodeString(IntPtr evt, nuint length, ushort[] characters);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, uint units, int wheelCount, int wheel1);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(uint tap, IntPtr evt);

    [DllImport(CoreGraphics)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreGraphics)]
    public static extern CGPoint CGEventGetLocation(IntPtr evt);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetLocation(IntPtr evt, CGPoint point);

    [DllImport(CoreGraphics)]
    public static extern void CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    [DllImport(ApplicationServices)]
    public static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    public static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    // kCGScrollEventUnitPixel = 0, kCGScrollEventUnitLine = 1
    public const uint kCGScrollEventUnitLine = 1;
}
