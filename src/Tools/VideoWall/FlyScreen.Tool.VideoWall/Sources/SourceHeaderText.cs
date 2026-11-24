namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>
/// 表单「自定义 Header」文本 ↔ 字典。
/// 每行一条 <c>Name: Value</c> 或 <c>Name=Value</c>；最多 <see cref="MaxEntries"/> 条。
/// </summary>
public static class SourceHeaderText
{
    public const int MaxEntries = 8;

    public static string Format(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return "";

        return string.Join(
            Environment.NewLine,
            headers
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .Take(MaxEntries)
                .Select(kv => $"{kv.Key.Trim()}: {kv.Value ?? ""}"));
    }

    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (result.Count >= MaxEntries)
                break;

            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var sep = line.IndexOf(':');
            var eq = line.IndexOf('=');
            if (sep < 0 || (eq >= 0 && eq < sep))
                sep = eq;
            if (sep <= 0)
                continue;

            var key = line[..sep].Trim();
            if (key.Length == 0)
                continue;

            var value = line[(sep + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }
}
