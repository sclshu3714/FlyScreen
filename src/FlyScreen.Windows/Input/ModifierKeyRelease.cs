using FlyScreen.Native;

namespace FlyScreen.Input;

/// <summary>
/// Ctrl+Alt+热键触发后，物理修饰键抬起可能被钩子吞掉或只发到对端，导致 Ctrl/Alt 粘滞。
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

    public static bool IsPhysicallyDown(ushort vk)
        => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
}
