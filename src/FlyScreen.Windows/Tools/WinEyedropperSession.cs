using System.Diagnostics;
using System.Runtime.InteropServices;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tools;

/// <summary>
/// 全局滴管：低层鼠标钩实时采样全屏任意像素；左键锁定、右键/Esc 取消。
/// 移动回调默认节流，避免拖垮 UI。
/// </summary>
public sealed class WinEyedropperSession : IDisposable
{
    private readonly IScreenColorSampler _sampler;
    private readonly Action<RgbaColor> _onMove;
    private readonly Action<RgbaColor> _onCommit;
    private readonly Action _onCancel;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyProc;
    private readonly int _moveThrottleMs;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyHook = IntPtr.Zero;
    private long _lastMoveTick;
    private bool _disposed;

    public WinEyedropperSession(
        IScreenColorSampler sampler,
        Action<RgbaColor> onMove,
        Action<RgbaColor> onCommit,
        Action onCancel,
        int moveThrottleMs = 33)
    {
        _sampler = sampler;
        _onMove = onMove;
        _onCommit = onCommit;
        _onCancel = onCancel;
        _moveThrottleMs = Math.Max(8, moveThrottleMs);
        _mouseProc = MouseHookCallback;
        _keyProc = KeyHookCallback;
        var mod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        if (mod == IntPtr.Zero)
            mod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = SetWindowsHookExKb(WH_KEYBOARD_LL, _keyProc, mod, 0);
        ApplyEyedropperCursors();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }

        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE); }
        catch { /* ignore */ }
    }

    private void ApplyEyedropperCursors()
    {
        // 十字准星近似滴管；复制句柄后再 SetSystemCursor（会销毁传入句柄）
        try
        {
            var cross = LoadCursor(IntPtr.Zero, IDC_CROSS);
            if (cross == IntPtr.Zero) return;
            foreach (var id in new uint[]
                     {
                         OCR_NORMAL, OCR_IBEAM, OCR_WAIT, OCR_CROSS, OCR_UP,
                         OCR_SIZENWSE, OCR_SIZENESW, OCR_SIZEWE, OCR_SIZENS, OCR_SIZEALL,
                         OCR_NO, OCR_HAND, OCR_APPSTARTING
                     })
            {
                var copy = CopyIcon(cross);
                if (copy != IntPtr.Zero)
                    SetSystemCursor(copy, id);
            }
        }
        catch { /* ignore */ }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_RBUTTONDOWN)
            {
                if (msg == WM_MOUSEMOVE)
                {
                    var now = Environment.TickCount64;
                    if (now - _lastMoveTick < _moveThrottleMs)
                        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                    _lastMoveTick = now;
                }

                if (_sampler.TrySample(info.pt.x, info.pt.y, out var color))
                {
                    if (msg == WM_MOUSEMOVE)
                        _onMove(color);
                    else if (msg == WM_LBUTTONDOWN)
                    {
                        _onCommit(color);
                        return (IntPtr)1;
                    }
                    else if (msg == WM_RBUTTONDOWN)
                    {
                        _onCancel();
                        return (IntPtr)1;
                    }
                }
                else if (msg == WM_RBUTTONDOWN)
                {
                    _onCancel();
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed && wParam.ToInt32() == WM_KEYDOWN)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk == VK_ESCAPE)
            {
                _onCancel();
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;
    private const int IDC_CROSS = 32515;
    private const uint OCR_NORMAL = 32512;
    private const uint OCR_IBEAM = 32513;
    private const uint OCR_WAIT = 32514;
    private const uint OCR_CROSS = 32515;
    private const uint OCR_UP = 32516;
    private const uint OCR_SIZENWSE = 32642;
    private const uint OCR_SIZENESW = 32643;
    private const uint OCR_SIZEWE = 32644;
    private const uint OCR_SIZENS = 32645;
    private const uint OCR_SIZEALL = 32646;
    private const uint OCR_NO = 32648;
    private const uint OCR_HAND = 32649;
    private const uint OCR_APPSTARTING = 32650;
    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_SENDCHANGE = 0x02;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExKb(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
