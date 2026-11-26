using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.Helpers;

/// <summary>
/// Windows 空闲抑制：SetThreadExecutionState 防息屏/睡眠，并周期性相对位移 0 重置屏保空闲计时。
/// </summary>
public static class WindowsIdleInhibit
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private static readonly object Gate = new();
    private static Timer? _timer;
    private static bool _active;

    public static void SetActive(bool active)
    {
        lock (Gate)
        {
            if (active)
            {
                _active = true;
                ApplyExecutionState(true);
                _timer ??= new Timer(_ => Pulse(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            else
            {
                _active = false;
                try { _timer?.Dispose(); } catch { /* ignore */ }
                _timer = null;
                ApplyExecutionState(false);
            }
        }
    }

    private static void Pulse()
    {
        lock (Gate)
        {
            if (!_active) return;
            ApplyExecutionState(true);
            TryNudgeMouse();
        }
    }

    private static void ApplyExecutionState(bool on)
    {
        try
        {
            if (on)
                SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
            else
                SetThreadExecutionState(EsContinuous);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>相对移动 0：重置 LastInput，避免屏保/部分锁屏策略仍按空闲触发。</summary>
    private static void TryNudgeMouse()
    {
        try
        {
            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = NativeMethods.MOUSEEVENTF_MOVE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
        }
        catch
        {
            // ignore
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
