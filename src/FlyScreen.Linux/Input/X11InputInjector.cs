using System.Runtime.InteropServices;
using FlyScreen.Linux.Environment;
using FlyScreen.Services.Network;

namespace FlyScreen.Linux.Input;

/// <summary>
/// X11 键鼠注入。绝对移动用 XWarpPointer（保证系统光标可见），按钮/滚轮/键用 XTest。
/// 真机需 libX11 / libXtst；非 Linux 或库缺失时 IsAvailable=false。
/// </summary>
public sealed class X11InputInjector : ILinuxInputInjector, IDisposable
{
    private const int XC_left_ptr = 68;

    private IntPtr _display;
    private IntPtr _root;
    private IntPtr _cursor;
    private bool _cursorEnsured;

    public string BackendName => "x11";
    public bool IsAvailable => _display != IntPtr.Zero;

    public X11InputInjector()
    {
        if (!LinuxRuntimeEnvironment.IsLinux || string.IsNullOrWhiteSpace(LinuxRuntimeEnvironment.Display))
            return;

        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return;

            _root = XDefaultRootWindow(_display);
            _ = XTestQueryExtension(_display, out _, out _, out _, out _);
            EnsureCursorVisible();
        }
        catch
        {
            _display = IntPtr.Zero;
            _root = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 在根窗口定义可见字体光标，并用 XFixes 取消隐藏（若可用）。
    /// 真实 Linux 与 WSLg/X11 均适用，避免「能点不能见」。
    /// </summary>
    public void EnsureCursorVisible()
    {
        if (!IsAvailable) return;
        try
        {
            if (_cursor == IntPtr.Zero)
                _cursor = XCreateFontCursor(_display, XC_left_ptr);
            if (_cursor != IntPtr.Zero && _root != IntPtr.Zero)
                _ = XDefineCursor(_display, _root, _cursor);

            TryXFixesShowCursor();
            _ = XFlush(_display);
            _cursorEnsured = true;
        }
        catch
        {
            // ignore
        }
    }

    public void MoveAbsoluteNormalized(double nx, double ny)
    {
        if (!IsAvailable) return;
        if (!_cursorEnsured) EnsureCursorVisible();

        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        try
        {
            var screen = XDefaultScreen(_display);
            var w = Math.Max(1, XDisplayWidth(_display, screen));
            var h = Math.Max(1, XDisplayHeight(_display, screen));
            var x = (int)Math.Round(nx * (w - 1));
            var y = (int)Math.Round(ny * (h - 1));

            // Warp 负责可见光标位置；XTest 补发运动事件给客户端
            _ = XWarpPointer(_display, IntPtr.Zero, _root, 0, 0, 0, 0, x, y);
            _ = XTestFakeMotionEvent(_display, screen, x, y, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void MoveRelative(int dx, int dy)
    {
        if (!IsAvailable) return;
        if (!_cursorEnsured) EnsureCursorVisible();
        try
        {
            // 相对移动：先查当前点再 Warp，保证光标可见（纯 XTest 相对在部分合成器上不画光标）
            if (XQueryPointer(_display, _root, out _, out _, out var rx, out var ry, out _, out _, out _) != 0)
            {
                var screen = XDefaultScreen(_display);
                var w = Math.Max(1, XDisplayWidth(_display, screen));
                var h = Math.Max(1, XDisplayHeight(_display, screen));
                var x = Math.Clamp(rx + dx, 0, w - 1);
                var y = Math.Clamp(ry + dy, 0, h - 1);
                _ = XWarpPointer(_display, IntPtr.Zero, _root, 0, 0, 0, 0, x, y);
                _ = XTestFakeMotionEvent(_display, screen, x, y, 0);
            }
            else
            {
                _ = XTestFakeRelativeMotionEvent(_display, dx, dy, 0);
            }

            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public bool TryGetPointerNormalized(out double nx, out double ny)
    {
        nx = ny = 0;
        if (!IsAvailable) return false;
        try
        {
            if (XQueryPointer(_display, _root, out _, out _, out var rx, out var ry, out _, out _, out _) == 0)
                return false;
            var screen = XDefaultScreen(_display);
            var w = Math.Max(1, XDisplayWidth(_display, screen) - 1);
            var h = Math.Max(1, XDisplayHeight(_display, screen) - 1);
            nx = Math.Clamp(rx / (double)w, 0, 1);
            ny = Math.Clamp(ry / (double)h, 0, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void MouseButton(MouseButtonPayload p)
    {
        if (!IsAvailable) return;
        uint button = p.Button switch { 1 => 3, 2 => 2, _ => 1 };
        try
        {
            _ = XTestFakeButtonEvent(_display, button, p.Down ? 1 : 0, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void MouseWheel(MouseWheelPayload p)
    {
        if (!IsAvailable) return;
        uint button = p.Delta > 0 ? 4u : 5u;
        var clicks = Math.Max(1, Math.Abs(p.Delta) / 120);
        try
        {
            for (var i = 0; i < clicks; i++)
            {
                _ = XTestFakeButtonEvent(_display, button, 1, 0);
                _ = XTestFakeButtonEvent(_display, button, 0, 0);
            }
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    public void Key(KeyEventPayload p)
    {
        if (!IsAvailable) return;
        if (!string.IsNullOrEmpty(p.Text))
        {
            InsertText(p.Text);
            return;
        }

        var keysym = MapVkToKeysym(p.VirtualKey);
        try
        {
            var keycode = XKeysymToKeycode(_display, keysym);
            if (keycode == 0) return;
            _ = XTestFakeKeyEvent(_display, keycode, p.Down ? 1 : 0, 0);
            _ = XFlush(_display);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 把码位临时绑到空闲键码再 XTest。拉丁键码的 shift 档对不上大小写，不能直接按已有 keycode。
    /// </summary>
    private void InsertText(string text)
    {
        lock (_unicodeGate)
        {
            try
            {
                foreach (var ch in text)
                {
                    if (ch == '\r') continue;
                    if (ch == '\n')
                    {
                        Key(new KeyEventPayload { VirtualKey = 0x0D, Down = true });
                        Key(new KeyEventPayload { VirtualKey = 0x0D, Down = false });
                        continue;
                    }

                    TypeCodePoint(ch);
                }

                _ = XFlush(_display);
            }
            catch { /* ignore */ }
        }
    }

    private readonly object _unicodeGate = new();
    private const int UnicodeSpareKeycode = 248;

    private void TypeCodePoint(int cp)
    {
        ulong keysym = cp < 0x100 ? (ulong)cp : 0x01000000UL + (ulong)cp;
        var per = 1;
        var oldPtr = XGetKeyboardMapping(_display, (byte)UnicodeSpareKeycode, 1, out per);
        ulong[]? backup = null;
        try
        {
            if (oldPtr != IntPtr.Zero && per > 0)
            {
                backup = new ulong[per];
                var width = IntPtr.Size;
                for (var i = 0; i < per; i++)
                {
                    backup[i] = width == 8
                        ? (ulong)Marshal.ReadInt64(oldPtr, i * width)
                        : (uint)Marshal.ReadInt32(oldPtr, i * width);
                }
            }

            var count = Math.Max(per, 1);
            var next = new ulong[count];
            next[0] = keysym;
            _ = XChangeKeyboardMapping(_display, UnicodeSpareKeycode, count, next, 1);
            _ = XSync(_display, false);
            _ = XTestFakeKeyEvent(_display, UnicodeSpareKeycode, 1, 0);
            _ = XTestFakeKeyEvent(_display, UnicodeSpareKeycode, 0, 0);
            _ = XSync(_display, false);
        }
        finally
        {
            if (oldPtr != IntPtr.Zero)
                _ = XFree(oldPtr);
            if (backup != null)
            {
                _ = XChangeKeyboardMapping(_display, UnicodeSpareKeycode, backup.Length, backup, 1);
                _ = XFlush(_display);
            }
        }
    }

    public void Dispose()
    {
        if (_display != IntPtr.Zero)
        {
            try
            {
                if (_cursor != IntPtr.Zero)
                {
                    _ = XFreeCursor(_display, _cursor);
                    _cursor = IntPtr.Zero;
                }
                _ = XCloseDisplay(_display);
            }
            catch { /* ignore */ }
            _display = IntPtr.Zero;
        }
    }

    private void TryXFixesShowCursor()
    {
        try
        {
            // 动态解析，避免未装 libXfixes 时启动失败
            var lib = NativeLibrary.Load("libXfixes.so.3");
            try
            {
                if (NativeLibrary.TryGetExport(lib, "XFixesShowCursor", out var fn) && _root != IntPtr.Zero)
                {
                    var show = Marshal.GetDelegateForFunctionPointer<XFixesShowCursorDelegate>(fn);
                    show(_display, _root);
                }
            }
            finally
            {
                // 保持进程内已加载即可，不强制 Free
            }
        }
        catch
        {
            // libXfixes 可选
        }
    }

    private static ulong MapVkToKeysym(ushort vk) => vk switch
    {
        0x08 => 0xff08, // BackSpace
        0x09 => 0xff09, // Tab
        0x0D => 0xff0d, // Return
        0x1B => 0xff1b, // Escape
        0x14 => 0xffe5, // Caps_Lock
        0x20 => 0x0020,
        0x21 => 0xff55, // Page_Up
        0x22 => 0xff56, // Page_Down
        0x23 => 0xff57, // End
        0x24 => 0xff50, // Home
        0x25 => 0xff51, // Left
        0x26 => 0xff52, // Up
        0x27 => 0xff53, // Right
        0x28 => 0xff54, // Down
        0x2E => 0xffff, // Delete
        0x10 or 0xA0 => 0xffe1, // Shift_L
        0xA1 => 0xffe2, // Shift_R
        0x11 or 0xA2 => 0xffe3, // Control_L
        0xA3 => 0xffe4, // Control_R
        0x12 or 0xA4 => 0xffe9, // Alt_L
        0xA5 => 0xffea, // Alt_R
        >= 0x41 and <= 0x5A => (ulong)(vk + 0x20),
        >= 0x30 and <= 0x39 => vk,
        _ => vk
    };

    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    private delegate void XFixesShowCursorDelegate(IntPtr display, IntPtr window);

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport(LibX11)] private static extern int XFree(IntPtr data);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
    [DllImport(LibX11)]
    private static extern IntPtr XGetKeyboardMapping(IntPtr display, byte firstKeycode, int keycodeCount, out int keysymsPerKeycode);
    [DllImport(LibX11)]
    private static extern int XChangeKeyboardMapping(IntPtr display, int firstKeycode, int keysymsPerKeycode, ulong[] keysyms, int numCodes);
    [DllImport(LibX11)] private static extern IntPtr XCreateFontCursor(IntPtr display, uint shape);
    [DllImport(LibX11)] private static extern int XDefineCursor(IntPtr display, IntPtr window, IntPtr cursor);
    [DllImport(LibX11)] private static extern int XFreeCursor(IntPtr display, IntPtr cursor);
    [DllImport(LibX11)]
    private static extern int XWarpPointer(
        IntPtr display, IntPtr src_w, IntPtr dest_w,
        int src_x, int src_y, uint src_width, uint src_height,
        int dest_x, int dest_y);
    [DllImport(LibX11)]
    private static extern int XQueryPointer(
        IntPtr display, IntPtr window,
        out IntPtr rootReturn, out IntPtr childReturn,
        out int rootX, out int rootY,
        out int winX, out int winY,
        out int mask);

    [DllImport(LibXtst)]
    private static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport(LibXtst)]
    private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);
    [DllImport(LibXtst)]
    private static extern int XTestFakeRelativeMotionEvent(IntPtr display, int dx, int dy, ulong delay);
    [DllImport(LibXtst)]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, ulong delay);
    [DllImport(LibXtst)]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, ulong delay);
}
