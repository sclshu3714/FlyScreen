using System.Globalization;
using System.Xml.Linq;

namespace FlyScreen.Tool.VideoWall.Sources.Dlna;

/// <summary>解析 ContentDirectory Browse 返回的 DIDL-Lite。</summary>
public static class DlnaDidlParser
{
    private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";

    public static IReadOnlyList<DlnaNode> Parse(string? didlXml, string fallbackParentId = "0")
    {
        if (string.IsNullOrWhiteSpace(didlXml))
            return [];

        XDocument doc;
        try
        {
            doc = XDocument.Parse(didlXml, LoadOptions.None);
        }
        catch
        {
            return [];
        }

        var root = doc.Root;
        if (root is null)
            return [];

        var list = new List<DlnaNode>();
        foreach (var el in root.Elements())
        {
            var local = el.Name.LocalName;
            if (local.Equals("container", StringComparison.OrdinalIgnoreCase))
            {
                var node = ParseContainer(el, fallbackParentId);
                if (node is not null)
                    list.Add(node);
            }
            else if (local.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                var node = ParseItem(el, fallbackParentId);
                if (node is not null)
                    list.Add(node);
            }
        }

        return list;
    }

    private static DlnaNode? ParseContainer(XElement el, string fallbackParentId)
    {
        var id = Attr(el, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var parent = Attr(el, "parentID") ?? fallbackParentId;
        var title = el.Element(Dc + "title")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = id;
        var upnpClass = el.Element(Upnp + "class")?.Value?.Trim();
        return new DlnaNode(id, parent, IsFolder: true, title, null, null, null, upnpClass);
    }

    private static DlnaNode? ParseItem(XElement el, string fallbackParentId)
    {
        var id = Attr(el, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var parent = Attr(el, "parentID") ?? fallbackParentId;
        var title = el.Element(Dc + "title")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = id;
        var upnpClass = el.Element(Upnp + "class")?.Value?.Trim() ?? "";

        var res = PickBestRes(el.Elements(Didl + "res").Concat(el.Elements().Where(e => e.Name.LocalName == "res")));
        var isVideo = upnpClass.Contains("video", StringComparison.OrdinalIgnoreCase)
                      || (res.Mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false)
                      || LooksLikeVideoUrl(res.Url);

        // 非视频 item（音乐/图片）跳过，避免挂墙噪音；无 res 的条目也跳过
        if (!isVideo || string.IsNullOrWhiteSpace(res.Url))
            return null;

        return new DlnaNode(id, parent, IsFolder: false, title!, res.Url, res.Mime, res.Size, upnpClass);
    }

    public static (string? Url, string? Mime, long? Size) PickBestRes(IEnumerable<XElement> resources)
    {
        string? bestUrl = null;
        string? bestMime = null;
        long? bestSize = null;
        var bestScore = -1;

        foreach (var res in resources)
        {
            var url = (res.Value ?? "").Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme is not ("http" or "https"))
                continue;

            var protocol = Attr(res, "protocolInfo") ?? "";
            var mime = ExtractMime(protocol);
            long? size = null;
            if (long.TryParse(Attr(res, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 0)
                size = parsed;

            var score = 0;
            if (protocol.Contains("http-get", StringComparison.OrdinalIgnoreCase))
                score += 10;
            if (mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
                score += 20;
            if (LooksLikeVideoUrl(url))
                score += 5;
            if (size is > 0)
                score += 1;

            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
                bestMime = mime;
                bestSize = size;
            }
        }

        return (bestUrl, bestMime, bestSize);
    }

    private static string? ExtractMime(string protocolInfo)
    {
        // http-get:*:video/mpeg:*
        var parts = protocolInfo.Split(':');
        if (parts.Length >= 3 && parts[2].Contains('/', StringComparison.Ordinal))
            return parts[2].Trim();
        return null;
    }

    private static bool LooksLikeVideoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var path = url;
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) path = path[..q];
        return path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".wmv", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mpg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Attr(XElement el, string name)
        => el.Attribute(name)?.Value
           ?? el.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
