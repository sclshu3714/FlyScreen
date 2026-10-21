using System.Text;

namespace FlyScreen.Services.Network;

/// <summary>把连续拼音拆成 zhong'wen'shu'ru 形式（已有引号则原样返回）。</summary>
public static class PinyinReadingFormatter
{
    // 常见声母（长的优先）
    private static readonly string[] Initials =
    [
        "zh", "ch", "sh", "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h",
        "j", "q", "x", "r", "z", "c", "s", "y", "w"
    ];

    // 常见韵母（长的优先）
    private static readonly string[] Finals =
    [
        "uang", "iang", "iong", "ueng", "uang", "uai", "uan", "iao", "ian", "ang", "eng", "ing", "ong",
        "ai", "ei", "ui", "ao", "ou", "iu", "ie", "üe", "ve", "er", "an", "en", "in", "un", "ün", "vn",
        "a", "o", "e", "i", "u", "ü", "v"
    ];

    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant().Replace(" ", "");
        if (s.Contains('\'')) return s;

        var parts = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            if (!TryMatchSyllable(s, i, out var len) || len <= 0)
            {
                // 无法匹配：余下整段作为一节
                parts.Add(s[i..]);
                break;
            }
            parts.Add(s.Substring(i, len));
            i += len;
        }

        return string.Join("'", parts);
    }

    private static bool TryMatchSyllable(string s, int start, out int length)
    {
        length = 0;
        var rem = s.Length - start;
        if (rem <= 0) return false;

        // 零声母：直接韵母
        foreach (var f in Finals)
        {
            if (f.Length > rem) continue;
            if (string.CompareOrdinal(s, start, f, 0, f.Length) != 0) continue;
            // 后面若还能接更长音节则继续；这里取最长韵母
            if (f.Length > length) length = f.Length;
        }

        foreach (var ini in Initials)
        {
            if (ini.Length > rem) continue;
            if (string.CompareOrdinal(s, start, ini, 0, ini.Length) != 0) continue;
            var after = start + ini.Length;
            var rem2 = s.Length - after;
            foreach (var f in Finals)
            {
                if (f.Length > rem2) continue;
                if (string.CompareOrdinal(s, after, f, 0, f.Length) != 0) continue;
                var total = ini.Length + f.Length;
                if (total > length) length = total;
            }
        }

        if (length <= 0)
        {
            length = 1; // 至少吃掉一个字母，避免死循环
            return true;
        }
        return true;
    }
}
