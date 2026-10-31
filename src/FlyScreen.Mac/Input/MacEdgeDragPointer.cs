using System.Runtime.InteropServices;
using FlyScreen.Mac.Native;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.Mac.Input;

/// <summary>轮询当前指针，直到左键松开。用来把拖拽继续带到布局里的下一台。</summary>
public sealed class MacEdgeDragPointer : IEdgeDragPointer
{
    private readonly MacInputInjector _injector;

    public MacEdgeDragPointer(MacInputInjector injector) => _injector = injector;

    public bool TryRead(out int x, out int y, out bool leftDown)
    {
        x = y = 0;
        leftDown = false;
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var ev = CgEventNative.CGEventCreate(IntPtr.Zero);
            if (ev == IntPtr.Zero) return false;
            var loc = CgEventNative.CGEventGetLocation(ev);
            CgEventNative.CFRelease(ev);
            x = (int)loc.X;
            y = (int)loc.Y;
            leftDown = CgEventNative.CGEventSourceButtonState(0, CgEventNative.kCGMouseButtonLeft);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void WarpToCenter()
    {
        if (!OperatingSystem.IsMacOS()) return;
        ScreenSize(out var w, out var h);
        try
        {
            CgEventNative.CGWarpMouseCursorPosition(new CgEventNative.CGPoint { X = w / 2.0, Y = h / 2.0 });
        }
        catch { /* ignore */ }
    }

    public void ScreenSize(out int width, out int height)
    {
        width = 1440;
        height = 900;
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var id = CgEventNative.CGMainDisplayID();
            width = Math.Max(1, (int)CgEventNative.CGDisplayPixelsWide(id));
            height = Math.Max(1, (int)CgEventNative.CGDisplayPixelsHigh(id));
        }
        catch { /* ignore */ }
    }

    public void DismissLocalDrag()
    {
        _injector.Key(new FlyScreen.Services.Network.KeyEventPayload { VirtualKey = 0x1B, Down = true });
        _injector.Key(new FlyScreen.Services.Network.KeyEventPayload { VirtualKey = 0x1B, Down = false });
    }
}
