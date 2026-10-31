using FlyScreen.Native;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Platforms.Windows;

public static class MauiInputInjector
{
    private static readonly IntPtr InjectMarker = new(0xF15C);

    public static bool IsInjected(IntPtr extraInfo) => extraInfo == InjectMarker;

    public const uint LLKHF_INJECTED = 0x00000010;

    public static bool IsInjectedMouse(uint flags, IntPtr extraInfo)
        => extraInfo == InjectMarker
           || (flags & (NativeMethods.LLMHF_INJECTED | NativeMethods.LLMHF_LOWER_IL_INJECTED)) != 0;

    public static bool IsInjectedKey(uint flags, IntPtr extraInfo)
        => extraInfo == InjectMarker
           || (flags & LLKHF_INJECTED) != 0;

    public static void MoveAbsoluteNormalized(double nx, double ny)
    {
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;

        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        // 0..65535 映射到整个虚拟桌面，并带注入标记，便于钩子区分物理鼠标
        var absX = (int)Math.Round(nx * 65535);
        var absY = (int)Math.Round(ny * 65535);
        SendMouse(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            absX, absY, 0);
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

    /// <summary>按 UTF-16 码元注入字符（KEYEVENTF_UNICODE），不经过键盘布局和剪贴板。</summary>
    public static void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var batch = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                FlushUnicode(batch);
                Key(new KeyEventPayload { VirtualKey = 0x0D, Down = true });
                Key(new KeyEventPayload { VirtualKey = 0x0D, Down = false });
                continue;
            }

            batch.Add(UnicodeInput(ch, up: false));
            batch.Add(UnicodeInput(ch, up: true));
        }

        FlushUnicode(batch);
    }

    private static NativeMethods.INPUT UnicodeInput(char ch, bool up)
        => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0u),
                    dwExtraInfo = InjectMarker
                }
            }
        };

    private static void FlushUnicode(List<NativeMethods.INPUT> batch)
    {
        if (batch.Count == 0) return;
        NativeMethods.SendInput((uint)batch.Count, batch.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        batch.Clear();
    }

    /// <summary>注入 Ctrl+V（或 Shift+Insert）完成粘贴。</summary>
    public static void InjectPaste(bool shiftInsert = false)
    {
        if (shiftInsert)
        {
            Key(new KeyEventPayload { VirtualKey = 0x10, Down = true }); // Shift
            Key(new KeyEventPayload { VirtualKey = 0x2D, Down = true, Extended = true }); // Insert
            Key(new KeyEventPayload { VirtualKey = 0x2D, Down = false, Extended = true });
            Key(new KeyEventPayload { VirtualKey = 0x10, Down = false });
        }
        else
        {
            Key(new KeyEventPayload { VirtualKey = 0x11, Down = true }); // Ctrl
            Key(new KeyEventPayload { VirtualKey = 0x56, Down = true }); // V
            Key(new KeyEventPayload { VirtualKey = 0x56, Down = false });
            Key(new KeyEventPayload { VirtualKey = 0x11, Down = false });
        }
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

