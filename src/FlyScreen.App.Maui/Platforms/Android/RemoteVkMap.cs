#if ANDROID
namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>WinVK → 可打印字符（无障碍未开或 IME 未产出时的回退）。</summary>
internal static class RemoteVkMap
{
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
