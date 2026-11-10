using FlyScreen.Native;

namespace FlyScreen.Input;

/// <summary>
/// Ctrl+Alt+热键触发后，物理修饰键抬起可能被钩子吞掉或只发到对端，导致 Ctrl/Alt 粘滞。
/// 另：热键成功后会对本机 SendInput 抬起修饰键，GetAsyncKeyState 会暂时报「已松开」，
/// 若用户仍按住再按 F2/1–4，热键会失效。故用钩子跟踪真实物理按下态。
/// </summary>
public static class ModifierKeyRelease
{
    /// <summary>左右 Ctrl/Alt/Shift/Win 及通用 VK。</summary>
    public static readonly (ushort Vk, bool Extended)[] All =
    [
        (0xA2, false), // VK_LCONTROL
        (0xA3, true),  // VK_RCONTROL
        (0x11, false), // VK_CONTROL
        (0xA4, false), // VK_LMENU
        (0xA5, true),  // VK_RMENU
        (0x12, false), // VK_MENU
        (0xA0, false), // VK_LSHIFT
        (0xA1, false), // VK_RSHIFT
        (0x10, false), // VK_SHIFT
        (0x5B, true),  // VK_LWIN
        (0x5C, true),  // VK_RWIN
    ];

    private static bool _lCtrl;
    private static bool _rCtrl;
    private static bool _lAlt;
    private static bool _rAlt;
    private static bool _lShift;
    private static bool _rShift;
    private static bool _lWin;
    private static bool _rWin;

    /// <summary>
    /// 仅对非注入的物理键调用。注入的 KeyUp 不得清状态，否则链式 Ctrl+Alt+F1→F2 会断。
    /// </summary>
    public static void ObservePhysicalKey(int vk, bool down)
    {
        switch (vk)
        {
            case 0xA2: // LCONTROL
                _lCtrl = down;
                break;
            case 0xA3: // RCONTROL
                _rCtrl = down;
                break;
            case 0x11: // CONTROL（未区分左右）
                if (down) _lCtrl = true;
                else _lCtrl = _rCtrl = false;
                break;
            case 0xA4: // LMENU
                _lAlt = down;
                break;
            case 0xA5: // RMENU
                _rAlt = down;
                break;
            case 0x12: // MENU
                if (down) _lAlt = true;
                else _lAlt = _rAlt = false;
                break;
            case 0xA0:
                _lShift = down;
                break;
            case 0xA1:
                _rShift = down;
                break;
            case 0x10:
                if (down) _lShift = true;
                else _lShift = _rShift = false;
                break;
            case 0x5B:
                _lWin = down;
                break;
            case 0x5C:
                _rWin = down;
                break;
        }
    }

    public static bool IsCtrlHeld() => _lCtrl || _rCtrl;
    public static bool IsAltHeld() => _lAlt || _rAlt;
    public static bool IsShiftHeld() => _lShift || _rShift;
    public static bool IsWinHeld() => _lWin || _rWin;
    public static bool IsCtrlAltHeld() => IsCtrlHeld() && IsAltHeld();

    public static bool IsPhysicallyDown(ushort vk)
        => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
}
