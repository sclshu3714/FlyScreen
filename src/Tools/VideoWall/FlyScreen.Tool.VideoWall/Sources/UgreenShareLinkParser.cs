namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>绿联 UGREENlink 分享页（网页壳，不是媒体文件）。</summary>
public sealed record UgreenShareLink
{
    /// <summary>分享 id（查询参数 <c>id</c> / <c>shareId</c>）。</summary>
    public required string ShareId { get; init; }

    /// <summary>门户路径里的设备别名；已经是设备域名时为 null。</summary>
    public string? Alias { get; init; }

    /// <summary>设备源站，例如 <c>https://alias.relay.ug.link</c>。门户链接在解析前为 null。</summary>
    public string? DeviceBase { get; init; }

    /// <summary>地址里自带的提取码（<c>password</c> / <c>pwd</c>）。</summary>
    public string? Password { get; init; }
}

/// <summary>
/// 识别 <c>https://ug.link/{别名}/filemgr/share-download/?id=</c>
/// 以及已经落到设备上的 <c>/filemgr/share-download/?id=</c>。
/// </summary>
public static class UgreenShareLinkParser
{
    public static bool IsSharePage(string? address)
        => TryParse(address, out _);

    public static bool TryParse(string? address, out UgreenShareLink link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(address))
            return false;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not "http" and not "https")
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var filemgr = IndexOf(segments, "filemgr");
        if (filemgr < 0 || filemgr + 1 >= segments.Length)
            return false;
        if (!segments[filemgr + 1].Equals("share-download", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("id", out var shareId) && !query.TryGetValue("shareId", out shareId))
            return false;
        if (string.IsNullOrWhiteSpace(shareId) || shareId.Length > 128)
            return false;

        query.TryGetValue("password", out var password);
        if (string.IsNullOrEmpty(password))
            query.TryGetValue("pwd", out password);

        var host = uri.IdnHost;
        var portal = host.Equals("ug.link", StringComparison.OrdinalIgnoreCase)
                     || host.Equals("www.ug.link", StringComparison.OrdinalIgnoreCase);

        string? alias = null;
        string? deviceBase = null;
        if (portal)
        {
            // /{alias}/filemgr/share-download
            if (filemgr != 1)
                return false;
            alias = segments[0];
            if (!IsAlias(alias))
                return false;
        }
        else
        {
            // 已经是设备地址：/filemgr/share-download
            if (filemgr != 0)
                return false;
            deviceBase = Origin(uri);
        }

        link = new UgreenShareLink
        {
            ShareId = shareId,
            Alias = alias,
            DeviceBase = deviceBase,
            Password = string.IsNullOrEmpty(password) ? null : password
        };
        return true;
    }

    private static bool IsAlias(string alias)
    {
        if (alias.Length is < 1 or > 64)
            return false;
        foreach (var c in alias)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }

        return true;
    }

    private static int IndexOf(string[] segments, string value)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return map;

        var raw = query[0] == '?' ? query[1..] : query;
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString(eq >= 0 ? part[..eq] : part);
            var value = eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : "";
            if (key.Length > 0)
                map[key] = value;
        }

        return map;
    }

    private static string Origin(Uri uri)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return origin.TrimEnd('/');
    }
}
