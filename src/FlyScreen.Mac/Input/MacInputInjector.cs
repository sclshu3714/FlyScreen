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
            if (!string.IsNullOrEmpty(p.Text))
            {
                InsertText(p.Text);
                return;
            }

            // VirtualKey 为 Windows VK
            var cgKey = MapVirtualKeyToCg(p.VirtualKey);
            var evt = CgEventNative.CGEventCreateKeyboardEvent(IntPtr.Zero, cgKey, p.Down);
            if (evt == IntPtr.Zero) return;
            CgEventNative.CGEventPost(CgEventNative.kCGHIDEventTap, evt);
            CgEventNative.CFRelease(evt);
        }
        catch { /* ignore */ }
    }

    private void InsertText(string text)
    {
        var units = new List<ushort>(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                FlushUnicode(units);
                Key(new KeyEventPayload { VirtualKey = 0x0D, Down = true });
                Key(new KeyEventPayload { VirtualKey = 0x0D, Down = false });
                continue;
            }

            units.Add(ch);
        }

        FlushUnicode(units);
    }

    private static void FlushUnicode(List<ushort> units)
    {
        if (units.Count == 0) return;
        var chars = units.ToArray();
        units.Clear();
        PostUnicode(chars, down: true);
        PostUnicode(chars, down: false);
    }

    private static void PostUnicode(ushort[] chars, bool down)
    {
        var evt = CgEventNative.CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
        if (evt == IntPtr.Zero) return;
        CgEventNative.CGEventKeyboardSetUnicodeString(evt, (nuint)chars.Length, chars);
        CgEventNative.CGEventPost(CgEventNative.kCGHIDEventTap, evt);
        CgEventNative.CFRelease(evt);
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
            0x14 => 57,  // Caps Lock
            0x20 => 49,  // Space
            0x21 => 116, // Page Up
            0x22 => 121, // Page Down
            0x23 => 119, // End
            0x24 => 115, // Home
            0x25 => 123, // Left
            0x26 => 126, // Up
            0x27 => 124, // Right
            0x28 => 125, // Down
            0x2E => 117, // Forward Delete
            0x10 or 0xA0 => 56, // Shift
            0xA1 => 60, // Right Shift
            0x11 or 0xA2 => 59, // Control
            0xA3 => 62, // Right Control
            0x12 or 0xA4 => 58, // Option
            0xA5 => 61, // Right Option
            0x41 => 0,  // A
            0x42 => 11, // B
            0x43 => 8,  // C
            0x44 => 2,  // D
            0x45 => 14, // E
            0x46 => 3,  // F
            0x47 => 5,  // G
            0x48 => 4,  // H
            0x49 => 34, // I
            0x4A => 38, // J
            0x4B => 40, // K
            0x4C => 37, // L
            0x4D => 46, // M
            0x4E => 45, // N
            0x4F => 31, // O
            0x50 => 35, // P
            0x51 => 12, // Q
            0x52 => 15, // R
            0x53 => 1,  // S
            0x54 => 17, // T
            0x55 => 32, // U
            0x56 => 9,  // V
            0x57 => 13, // W
            0x58 => 7,  // X
            0x59 => 16, // Y
            0x5A => 6,  // Z
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
