using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.LockInput;

/// <summary>
/// 尽量切到当前输入桌面（锁屏时常为 Winlogon）再 SendInput。
/// 在 SYSTEM 服务 / 控制台会话线程中调用效果最好。
/// </summary>
public static class SecureDesktopInjector
{
    private static readonly IntPtr InjectMarker = new(0xF15C);
    private static readonly object Gate = new();

    public static void MoveAbsoluteNormalized(double nx, double ny)
    {
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        var absX = (int)Math.Round(nx * 65535);
        var absY = (int)Math.Round(ny * 65535);
        SendMouse(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            absX, absY, 0);
    }

    public static void MoveRelative(int dx, int dy)
        => SendMouse(NativeMethods.MOUSEEVENTF_MOVE, dx, dy, 0);

    public static void MouseButton(int button, bool down)
    {
        uint flags = (button, down) switch
        {
            (0, true) => NativeMethods.MOUSEEVENTF_LEFTDOWN,
            (0, false) => NativeMethods.MOUSEEVENTF_LEFTUP,
            (1, true) => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
            (1, false) => NativeMethods.MOUSEEVENTF_RIGHTUP,
            (2, true) => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            _ => 0
        };
        if (flags == 0) return;
        SendMouse(flags, 0, 0, 0);
    }

    public static void MouseWheel(int delta, bool horizontal)
    {
        var flags = horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL;
        SendMouse(flags, 0, 0, unchecked((uint)delta));
    }

    public static void Key(ushort vk, bool down, bool extended)
    {
        WithInputDesktop(() =>
        {
            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)NativeMethods.MapVirtualKey(vk, 0),
                        dwFlags = (down ? 0u : NativeMethods.KEYEVENTF_KEYUP)
                                  | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u),
                        dwExtraInfo = InjectMarker
                    }
                }
            };
            NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
        });
    }

    public static void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        WithInputDesktop(() =>
        {
            var batch = new List<NativeMethods.INPUT>(text.Length * 2);
            foreach (var ch in text)
            {
                batch.Add(UnicodeKey(ch, down: true));
                batch.Add(UnicodeKey(ch, down: false));
            }

            NativeMethods.SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        });
    }

    private static NativeMethods.INPUT UnicodeKey(char ch, bool down)
        => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE | (down ? 0u : NativeMethods.KEYEVENTF_KEYUP),
                    dwExtraInfo = InjectMarker
                }
            }
        };

    private static void SendMouse(uint flags, int dx, int dy, uint data)
    {
        WithInputDesktop(() =>
        {
            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = data,
                        dwFlags = flags,
                        dwExtraInfo = InjectMarker
                    }
                }
            };
            NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
        });
    }

    private static void WithInputDesktop(Action action)
    {
        lock (Gate)
        {
            var desktop = OpenInputDesktop(0, false, DesktopGenericAll);
            if (desktop == IntPtr.Zero)
                desktop = OpenDesktopW("Winlogon", 0, false, DesktopGenericAll);
            if (desktop == IntPtr.Zero)
                desktop = OpenDesktopW("Default", 0, false, DesktopGenericAll);

            var old = IntPtr.Zero;
            try
            {
                if (desktop != IntPtr.Zero)
                {
                    old = GetThreadDesktop(GetCurrentThreadId());
                    SetThreadDesktop(desktop);
                }

                action();
            }
            finally
            {
                if (old != IntPtr.Zero)
                {
                    try { SetThreadDesktop(old); } catch { /* ignore */ }
                }

                if (desktop != IntPtr.Zero)
                {
                    try { CloseDesktop(desktop); } catch { /* ignore */ }
                }
            }
        }
    }

    private const uint DesktopGenericAll = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
