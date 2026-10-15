using System.Runtime.InteropServices;
using FlyScreen.Mac.Native;
using FlyScreen.Mac.Permissions;
using FlyScreen.Services.Network;

namespace FlyScreen.Mac.Input;

/// <summary>通过 CGEvent 注入键鼠。非 macOS 或未授权时为 no-op。</summary>
public sealed class MacInputInjector : IMacInputInjector
{
    public bool IsAvailable => AccessibilityPermission.IsMacOS && AccessibilityPermission.IsTrusted();

    public void MoveAbsoluteNormalized(double nx, double ny)
    {
        if (!AccessibilityPermission.IsMacOS) return;
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        // 主屏像素近似；多显示器后续用 CGDisplay
        var w = 1920.0;
        var h = 1080.0;
        try
        {
            // 使用当前事件位置所在屏的粗略尺寸：先 warp 到归一化坐标
            GetMainDisplaySize(out w, out h);
        }
        catch { /* keep default */ }

        var pt = new CgEventNative.CGPoint { X = nx * Math.Max(1, w - 1), Y = ny * Math.Max(1, h - 1) };
        PostMouse(CgEventNative.kCGEventMouseMoved, pt, CgEventNative.kCGMouseButtonLeft);
        try { CgEventNative.CGWarpMouseCursorPosition(pt); } catch { /* ignore */ }
    }

    public void MoveRelative(int dx, int dy)
    {
        if (!AccessibilityPermission.IsMacOS) return;
        try
        {
            using var probe = new EventHandle(CgEventNative.CGEventCreate(IntPtr.Zero));
            if (probe.Ptr == IntPtr.Zero) return;
            var loc = CgEventNative.CGEventGetLocation(probe.Ptr);
            loc.X += dx;
            loc.Y += dy;
            PostMouse(CgEventNative.kCGEventMouseMoved, loc, CgEventNative.kCGMouseButtonLeft);
            CgEventNative.CGWarpMouseCursorPosition(loc);
        }
        catch { /* ignore */ }
    }

    public void MouseButton(MouseButtonPayload p)
    {
        if (!AccessibilityPermission.IsMacOS) return;
        var (downType, upType, button) = p.Button switch
        {
            1 => (CgEventNative.kCGEventRightMouseDown, CgEventNative.kCGEventRightMouseUp, CgEventNative.kCGMouseButtonRight),
            2 => (CgEventNative.kCGEventOtherMouseDown, CgEventNative.kCGEventOtherMouseUp, CgEventNative.kCGMouseButtonCenter),
            _ => (CgEventNative.kCGEventLeftMouseDown, CgEventNative.kCGEventLeftMouseUp, CgEventNative.kCGMouseButtonLeft)
        };
        try
        {
            using var probe = new EventHandle(CgEventNative.CGEventCreate(IntPtr.Zero));
            var loc = probe.Ptr != IntPtr.Zero
                ? CgEventNative.CGEventGetLocation(probe.Ptr)
                : new CgEventNative.CGPoint();
            PostMouse(p.Down ? downType : upType, loc, button);
        }
        catch { /* ignore */ }
    }

    public void MouseWheel(MouseWheelPayload p)
    {
        if (!AccessibilityPermission.IsMacOS) return;
        try
        {
            // Windows 滚轮约 120/格；CG 用行单位，粗略 /120
            var lines = p.Delta / 120;
            if (lines == 0) lines = Math.Sign(p.Delta);
            var evt = CgEventNative.CGEventCreateScrollWheelEvent(
                IntPtr.Zero, CgEventNative.kCGScrollEventUnitLine, 1, lines);
            if (evt == IntPtr.Zero) return;
            CgEventNative.CGEventPost(CgEventNative.kCGHIDEventTap, evt);
            CgEventNative.CFRelease(evt);
        }
        catch { /* ignore */ }
    }

    public void Key(KeyEventPayload p)
    {
        if (!AccessibilityPermission.IsMacOS) return;
        try
        {
            // VirtualKey 为 Windows VK；完整映射表后续补齐。常用字母区粗映射。
            var cgKey = MapVirtualKeyToCg(p.VirtualKey);
            var evt = CgEventNative.CGEventCreateKeyboardEvent(IntPtr.Zero, cgKey, p.Down);
            if (evt == IntPtr.Zero) return;
            CgEventNative.CGEventPost(CgEventNative.kCGHIDEventTap, evt);
            CgEventNative.CFRelease(evt);
        }
        catch { /* ignore */ }
    }

    private static void PostMouse(uint type, CgEventNative.CGPoint pt, uint button)
    {
        var evt = CgEventNative.CGEventCreateMouseEvent(IntPtr.Zero, type, pt, button);
        if (evt == IntPtr.Zero) return;
        CgEventNative.CGEventPost(CgEventNative.kCGHIDEventTap, evt);
        CgEventNative.CFRelease(evt);
    }

    private static void GetMainDisplaySize(out double w, out double h)
    {
        // 简化：固定读主屏；完整多屏用 NSScreen / CGGetActiveDisplayList
        w = 1920;
        h = 1080;
        _ = RuntimeInformation.OSDescription;
    }

    /// <summary>Windows VK → macOS 虚拟键码（部分）。未映射则原样尝试。</summary>
    private static ushort MapVirtualKeyToCg(ushort vk)
    {
        // 字母 A-Z：Win VK 0x41-0x5A 与 mac keycode 不同，此处仅占位直通数字区等
        return vk switch
        {
            0x08 => 51,  // Backspace → Delete
            0x09 => 48,  // Tab
            0x0D => 36,  // Return
            0x1B => 53,  // Escape
            0x20 => 49,  // Space
            0x25 => 123, // Left
            0x26 => 126, // Up
            0x27 => 124, // Right
            0x28 => 125, // Down
            _ => vk
        };
    }

    private sealed class EventHandle : IDisposable
    {
        public IntPtr Ptr { get; }
        public EventHandle(IntPtr ptr) => Ptr = ptr;
        public void Dispose()
        {
            if (Ptr != IntPtr.Zero)
            {
                try { CgEventNative.CFRelease(Ptr); } catch { /* ignore */ }
            }
        }
    }
}
