#if ANDROID
namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>WinVK → 字符 / Android Keycode。</summary>
internal static class RemoteVkMap
{
    public static int ToAndroidKeyCode(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A => 29 + (vk - 0x41),
        >= 0x30 and <= 0x39 => 7 + (vk - 0x30),
        >= 0x60 and <= 0x69 => 7 + (vk - 0x60),
        0x20 => 62,
        0x08 => 67,
        0x2E => 112,
        0x0D => 66,
        0x09 => 61,
        0x25 => 21,
        0x26 => 19,
        0x27 => 22,
        0x28 => 20,
        0x10 or 0xA0 or 0xA1 => 59, // ShiftLeft
        0x6A => 17,
        0x6B => 81,
        0x6D => 69,
        0x6E => 56,
        0x6F => 76,
        0xBA => 74,
        0xBB => 70,
        0xBC => 55,
        0xBD => 69,
        0xBE => 56,
        0xBF => 76,
        0xC0 => 68,
        0xDB => 71,
        0xDC => 73,
        0xDD => 72,
        0xDE => 75,
        _ => -1
    };

    public static string? ToChar(ushort vk, bool shift)
    {
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)(shift ? vk : vk + 32)).ToString();
        if (vk is >= 0x30 and <= 0x39)
        {
            if (!shift) return ((char)vk).ToString();
            return (vk - 0x30) switch
            {
                0 => ")",
                1 => "!",
                2 => "@",
                3 => "#",
                4 => "$",
                5 => "%",
                6 => "^",
                7 => "&",
                8 => "*",
                9 => "(",
                _ => null
            };
        }
        if (vk is >= 0x60 and <= 0x69)
            return ((char)('0' + (vk - 0x60))).ToString();
        return vk switch
        {
            0x20 => " ",
            0x6A => "*",
            0x6B => "+",
            0x6D => "-",
            0x6E => ".",
            0x6F => "/",
            0xBA => shift ? ":" : ";",
            0xBB => shift ? "+" : "=",
            0xBC => shift ? "<" : ",",
            0xBD => shift ? "_" : "-",
            0xBE => shift ? ">" : ".",
            0xBF => shift ? "?" : "/",
            0xC0 => shift ? "~" : "`",
            0xDB => shift ? "{" : "[",
            0xDC => shift ? "|" : "\\",
            0xDD => shift ? "}" : "]",
            0xDE => shift ? "\"" : "'",
            _ => null
        };
    }
}
#endif
