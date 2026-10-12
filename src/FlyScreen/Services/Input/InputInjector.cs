using FlyScreen.Native;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Input;

public static class InputInjector
{
    private static readonly IntPtr InjectMarker = new(0xF15C);

    public static bool IsInjected(IntPtr extraInfo) => extraInfo == InjectMarker;

    public static void MoveAbsoluteNormalized(double nx, double ny)
    {
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;

        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        var x = vx + (int)Math.Round(nx * (vw - 1));
        var y = vy + (int)Math.Round(ny * (vh - 1));
        NativeMethods.SetCursorPos(x, y);
    }

    public static void MoveRelative(int dx, int dy)
    {
        SendMouse(NativeMethods.MOUSEEVENTF_MOVE, dx, dy, 0);
    }

    public static void MouseButton(MouseButtonPayload p)
    {
        uint flags = (p.Button, p.Down) switch
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

    public static void MouseWheel(MouseWheelPayload p)
    {
        var flags = p.Horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL;
        SendMouse(flags, 0, 0, unchecked((uint)p.Delta));
    }

    public static void Key(KeyEventPayload p)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = p.VirtualKey,
                    wScan = (ushort)NativeMethods.MapVirtualKey(p.VirtualKey, 0),
                    dwFlags = (p.Down ? 0u : NativeMethods.KEYEVENTF_KEYUP)
                              | (p.Extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u),
                    dwExtraInfo = InjectMarker
                }
            }
        };
        NativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendMouse(uint flags, int dx, int dy, uint data)
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
        NativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
