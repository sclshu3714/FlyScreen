namespace FlyScreen.ToolSdk;

/// <summary>
/// Builds plugin localization tables covering every <see cref="Cultures"/> entry,
/// with keys aligned to the zh-CN dictionary.
/// </summary>
public static class ToolLocalizationBuilder
{
    public static readonly string[] Cultures =
    [
        "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "pt-BR", "ru-RU",
        "ar-SA", "hi-IN", "th-TH", "vi-VN", "id-ID", "tr-TR", "it-IT", "nl-NL", "pl-PL", "uk-UA"
    ];

    /// <summary>
    /// zh + en required. extras: culture → full or partial key map (missing keys fall back en, then zh).
    /// Result always includes every Cultures entry with every zh key present.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build(
        IReadOnlyDictionary<string, string> zh,
        IReadOnlyDictionary<string, string> en,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? extras = null)
    {
        ArgumentNullException.ThrowIfNull(zh);
        ArgumentNullException.ThrowIfNull(en);

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in Cultures)
        {
            IReadOnlyDictionary<string, string>? overlay = null;
            extras?.TryGetValue(culture, out overlay);

            Dictionary<string, string> map;
            if (string.Equals(culture, "zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                map = new Dictionary<string, string>(zh, StringComparer.OrdinalIgnoreCase);
            }
            else if (string.Equals(culture, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                map = new Dictionary<string, string>(en, StringComparer.OrdinalIgnoreCase);
            }
            else if (string.Equals(culture, "zh-TW", StringComparison.OrdinalIgnoreCase) && overlay is null)
            {
                map = new Dictionary<string, string>(zh, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                map = new Dictionary<string, string>(en, StringComparer.OrdinalIgnoreCase);
            }

            if (overlay is not null)
            {
                foreach (var (key, value) in overlay)
                    map[key] = value;
            }

            foreach (var (key, zhVal) in zh)
            {
                if (map.ContainsKey(key))
                    continue;
                if (en.TryGetValue(key, out var enVal))
                    map[key] = enVal;
                else
                    map[key] = zhVal;
            }

            // Keep exactly the zh-CN key set (drop extras-only keys).
            var aligned = new Dictionary<string, string>(zh.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var key in zh.Keys)
            {
                if (map.TryGetValue(key, out var value))
                    aligned[key] = value;
                else if (en.TryGetValue(key, out var enVal))
                    aligned[key] = enVal;
                else
                    aligned[key] = zh[key];
            }

            result[culture] = aligned;
        }

        return result;
    }
}
