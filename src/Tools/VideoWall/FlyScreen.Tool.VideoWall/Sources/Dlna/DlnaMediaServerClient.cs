using System.Net;
using System.Text;
using System.Xml.Linq;

namespace FlyScreen.Tool.VideoWall.Sources.Dlna;

/// <summary>拉设备描述并 Browse ContentDirectory。</summary>
public sealed class DlnaMediaServerClient : IDisposable
{
    private const string ContentDirectoryType = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const int DefaultPageSize = 50;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public DlnaMediaServerClient(HttpClient? http = null)
    {
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            })
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    /// <summary>从 SSDP LOCATION 解析服务器与 ContentDirectory controlURL。</summary>
    public async Task<DlnaBoundServer> BindAsync(Uri location, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(location, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseDescription(location, xml);
    }

    /// <summary>对 SSDP 命中列表拉描述并去重（按 UDN）。</summary>
    public async Task<IReadOnlyList<DlnaBoundServer>> BindManyAsync(
        IEnumerable<DlnaSsdpHit> hits,
        CancellationToken ct = default)
    {
        var map = new Dictionary<string, DlnaBoundServer>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bound = await BindAsync(hit.Location, ct).ConfigureAwait(false);
                map[bound.Server.Udn] = bound;
            }
            catch
            {
                // 单台失败不影响其余
            }
        }

        return map.Values
            .OrderBy(s => s.Server.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<DlnaBrowsePage> BrowseAsync(
        DlnaBoundServer server,
        string objectId,
        int startIndex = 0,
        int requestedCount = DefaultPageSize,
        CancellationToken ct = default)
        => BrowseCoreAsync(server.ControlUrl, objectId, startIndex, requestedCount, ct);

    public static DlnaBoundServer ParseDescription(Uri location, string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("设备描述不是合法 XML", ex);
        }

        var device = FindDevice(doc.Root)
                     ?? throw new InvalidOperationException("设备描述缺少 device");

        var friendly = ElementValue(device, "friendlyName") ?? location.Host;
        var udn = ElementValue(device, "UDN") ?? location.AbsoluteUri;
        var manufacturer = ElementValue(device, "manufacturer");
        var model = ElementValue(device, "modelName");
        var baseUrl = ResolveBaseUrl(location, doc);

        var control = FindContentDirectoryControlUrl(device, baseUrl)
                      ?? throw new InvalidOperationException("未找到 ContentDirectory 服务");

        var server = new DlnaServer(udn, friendly, location, manufacturer, model);
        return new DlnaBoundServer(server, control);
    }

    private async Task<DlnaBrowsePage> BrowseCoreAsync(
        Uri controlUrl,
        string objectId,
        int startIndex,
        int requestedCount,
        CancellationToken ct)
    {
        if (requestedCount <= 0)
            requestedCount = DefaultPageSize;
        if (startIndex < 0)
            startIndex = 0;

        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            "<u:Browse xmlns:u=\"" + ContentDirectoryType + "\">" +
            "<ObjectID>" + XmlEscape(objectId) + "</ObjectID>" +
            "<BrowseFlag>BrowseDirectChildren</BrowseFlag>" +
            "<Filter>*</Filter>" +
            "<StartingIndex>" + startIndex + "</StartingIndex>" +
            "<RequestedCount>" + requestedCount + "</RequestedCount>" +
            "<SortCriteria></SortCriteria>" +
            "</u:Browse>" +
            "</s:Body></s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{ContentDirectoryType}#Browse\"");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Browse HTTP {(int)response.StatusCode}");

        return ParseBrowseResponse(text, objectId, startIndex, requestedCount);
    }

    public static DlnaBrowsePage ParseBrowseResponse(
        string soapXml,
        string objectId,
        int startIndex,
        int requestedCount)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(soapXml);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Browse 响应不是合法 XML", ex);
        }

        var resultEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Result");
        var didl = resultEl?.Value ?? "";
        var nodes = DlnaDidlParser.Parse(didl, objectId);

        var numberReturned = IntDesc(doc, "NumberReturned", nodes.Count);
        var totalMatches = IntDesc(doc, "TotalMatches", startIndex + numberReturned);
        var hasMore = startIndex + numberReturned < totalMatches
                      || (numberReturned >= requestedCount && totalMatches == 0);

        // 文件夹在前，视频在后
        var ordered = nodes
            .OrderByDescending(n => n.IsFolder)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DlnaBrowsePage(ordered, numberReturned, totalMatches, hasMore);
    }

    private static int IntDesc(XDocument doc, string localName, int fallback)
    {
        var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
        return int.TryParse(el?.Value, out var n) ? n : fallback;
    }

    private static XElement? FindDevice(XElement? root)
    {
        if (root is null) return null;
        if (root.Name.LocalName.Equals("device", StringComparison.OrdinalIgnoreCase))
            return root;
        return root.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
    }

    private static Uri ResolveBaseUrl(Uri location, XDocument doc)
    {
        var baseEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase");
        if (baseEl is not null
            && !string.IsNullOrWhiteSpace(baseEl.Value)
            && Uri.TryCreate(baseEl.Value.Trim(), UriKind.Absolute, out var absolute))
            return absolute;

        return new Uri(location.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static Uri? FindContentDirectoryControlUrl(XElement device, Uri baseUrl)
    {
        foreach (var service in device.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = ElementValue(service, "serviceType") ?? "";
            if (!type.Contains("ContentDirectory", StringComparison.OrdinalIgnoreCase))
                continue;
            var control = ElementValue(service, "controlURL");
            if (string.IsNullOrWhiteSpace(control))
                continue;
            return new Uri(baseUrl, control);
        }

        // 嵌入式 device 再找一遍（FindDevice 已在根；有些把 MediaServer 嵌在 root device 下）
        foreach (var nested in device.Descendants().Where(e => e.Name.LocalName == "device"))
        {
            if (ReferenceEquals(nested, device)) continue;
            var nestedType = ElementValue(nested, "deviceType") ?? "";
            if (!nestedType.Contains("MediaServer", StringComparison.OrdinalIgnoreCase))
                continue;
            var found = FindContentDirectoryControlUrl(nested, baseUrl);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static string? ElementValue(XElement parent, string localName)
        => parent.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim();

    private static string XmlEscape(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}

/// <summary>已绑定 ContentDirectory 的服务器。</summary>
public sealed record DlnaBoundServer(DlnaServer Server, Uri ControlUrl);
