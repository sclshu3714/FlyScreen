using System.Net;
using System.Text;
using System.Text.Json;
using FlyScreen.Services;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>绿联分享页无法直接交给播放器时抛出。</summary>
public sealed class UgreenShareException : Exception
{
    public UgreenShareException(VideoErrorCode code, string message) : base(message)
    {
        Code = code;
    }

    public VideoErrorCode Code { get; }
}

/// <summary>
/// 把 UGREENlink 分享页解析成可拉流的地址。
/// 分享 URL 本身是一段 HTML；真正的视频在设备上的
/// <c>/ugreen/v1/filemgr/shareMediaPlayer</c>，并且要带分享 Cookie、<c>UG-Agent</c> 和 Referer。
/// </summary>
public static class UgreenShareResolver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "avi", "mov", "webm", "flv", "ts", "m4v", "wmv", "mpeg", "mpg", "m2ts", "3gp"
    };

    private static readonly HttpClient Api = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8)
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static async Task<VideoEndpoint> PrepareAsync(VideoEndpoint endpoint, CancellationToken ct = default)
    {
        if (!UgreenShareLinkParser.TryParse(endpoint.Address, out var link))
            return endpoint;

        var deviceBase = await ResolveDeviceBaseAsync(link, ct).ConfigureAwait(false);
        using var infoDoc = await GetJsonAsync(
            Api,
            $"{deviceBase}/ugreen/v1/filemgr/queryShareInfo?share_id={Uri.EscapeDataString(link.ShareId)}",
            null,
            deviceHeaders: true,
            ct).ConfigureAwait(false);
        var info = RequireData(infoDoc);

        if (Bool(info, "expired"))
            throw new UgreenShareException(VideoErrorCode.SourceNotFound, "绿联分享已过期");
        if (Bool(info, "file_not_exist"))
            throw new UgreenShareException(VideoErrorCode.SourceNotFound, "绿联分享的文件不存在");
        if (Bool(info, "visit_limit_reached"))
            throw new UgreenShareException(VideoErrorCode.AuthForbidden, "绿联分享访问次数已用完");

        var shareType = Int(info, "share_type");
        if (shareType == 1)
            throw new UgreenShareException(VideoErrorCode.SourceUnsupported, "这是绿联设备内部分享，需要登录后才能播放");

        var hasPassword = Bool(info, "has_password");
        if (hasPassword && string.IsNullOrEmpty(link.Password))
            throw new UgreenShareException(VideoErrorCode.AuthRequired, "该绿联分享需要提取码，请在地址中加上 password 参数");

        var verifyBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["share_id"] = link.ShareId,
            ["password"] = link.Password ?? "",
            ["token"] = "",
            ["no_count"] = true
        });
        using var verifyDoc = await SendJsonAsync(
            HttpMethod.Post,
            $"{deviceBase}/ugreen/v1/filemgr/externalVerifySharePassword",
            verifyBody,
            cookie: null,
            ct).ConfigureAwait(false);

        var setCookie = verifyDoc.Cookie;
        var cookie = ReadShareCookie(setCookie, link.ShareId);
        var verify = RequireData(verifyDoc.Document);
        var files = await ListVideosAsync(deviceBase, link, cookie, verify, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            if (hasPassword)
                throw new UgreenShareException(VideoErrorCode.AuthRequired, "绿联分享提取码不正确，或分享里没有可播放的视频");
            throw new UgreenShareException(VideoErrorCode.SourceUnsupported, "绿联分享里没有可播放的视频");
        }

        var file = files[0];
        var mediaUrl =
            $"{deviceBase}/ugreen/v1/filemgr/shareMediaPlayer?path={Uri.EscapeDataString(file.Path)}&code=0";
        var referer = $"{deviceBase}/filemgr/share-download/?id={Uri.EscapeDataString(link.ShareId)}";

        var headers = new Dictionary<string, string>(endpoint.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = cookie,
            ["Referer"] = referer,
            ["UG-Agent"] = "PC/WEB",
            ["User-Agent"] = "Mozilla/5.0"
        };

        AppLog.Info($"视频墙：绿联分享已解析 host={new Uri(deviceBase).Host} file={file.Name}");
        return endpoint with
        {
            Protocol = "https",
            Address = mediaUrl,
            Headers = headers
        };
    }

    private readonly record struct ListedFile(string Name, string Path);

    private readonly record struct JsonReply(JsonDocument Document, string? Cookie) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private static async Task<string> ResolveDeviceBaseAsync(UgreenShareLink link, CancellationToken ct)
    {
        var bases = new List<string>();
        if (!string.IsNullOrWhiteSpace(link.DeviceBase))
            bases.Add(link.DeviceBase);

        if (!string.IsNullOrWhiteSpace(link.Alias))
        {
            try
            {
                foreach (var candidate in await LookupAliasAsync(link.Alias, ct).ConfigureAwait(false))
                {
                    if (!bases.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        bases.Add(candidate);
                }
            }
            catch (UgreenShareException) when (bases.Count > 0)
            {
                // 地址里已经有设备源站时，云端查询失败仍可直连
            }
        }

        if (bases.Count == 0)
            throw new UgreenShareException(VideoErrorCode.SourceNotFound, "找不到这个绿联设备");

        foreach (var candidate in bases.Take(4))
        {
            if (await HeartbeatAsync(candidate, ct).ConfigureAwait(false))
                return candidate.TrimEnd('/');
        }

        throw new UgreenShareException(VideoErrorCode.NetTimeout, "连不上绿联设备");
    }

    private static async Task<List<string>> LookupAliasAsync(string alias, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(
            HttpMethod.Post,
            "https://api.ugnas.com/api/p2p/v2/ta/nodeInfo/byAlias",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["alias"] = alias }),
            cookie: null,
            ct,
            portal: true).ConfigureAwait(false);
        var data = RequireData(doc.Document);
        var list = new List<string>();
        var relay = Str(data, "relayDomain");
        if (!string.IsNullOrWhiteSpace(relay))
            list.Add($"https://{alias}.{relay}");

        var port = 9443;
        if (data.TryGetProperty("netInfo", out var net) && net.ValueKind == JsonValueKind.Object)
        {
            port = Int(net, "httpsPort", 9443);
            if (port <= 0) port = 9443;
            AddHosts(list, net, "ddns", port);
            if (net.TryGetProperty("smartdns", out var smart) && smart.ValueKind == JsonValueKind.Object)
                AddHosts(list, smart, "lan", port);
        }

        if (list.Count == 0)
            throw new UgreenShareException(VideoErrorCode.SourceNotFound, "找不到这个绿联设备");
        return list;
    }

    private static void AddHosts(List<string> list, JsonElement obj, string name, int port)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var host = item.GetString();
            if (string.IsNullOrWhiteSpace(host))
                continue;
            var baseUrl = host.Contains("://", StringComparison.Ordinal)
                ? host.TrimEnd('/')
                : $"https://{host}:{port}";
            if (!list.Contains(baseUrl, StringComparer.OrdinalIgnoreCase))
                list.Add(baseUrl);
        }
    }

    private static async Task<bool> HeartbeatAsync(string deviceBase, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var doc = await GetJsonAsync(
                Api,
                $"{deviceBase.TrimEnd('/')}/ugreen/v1/verify/heartbeat",
                cookie: null,
                deviceHeaders: true,
                timeout.Token).ConfigureAwait(false);
            return Int(doc.RootElement, "code") == 200;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or UgreenShareException or JsonException)
        {
            return false;
        }
    }

    private static async Task<List<ListedFile>> ListVideosAsync(
        string deviceBase,
        UgreenShareLink link,
        string cookie,
        JsonElement verifyData,
        CancellationToken ct)
    {
        var videos = PickVideos(ArrayProp(verifyData, "file_info"));
        if (videos.Count > 0)
            return videos;

        var folders = PickFolders(ArrayProp(verifyData, "file_info"));
        if (folders.Count == 0)
            return videos;

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["token"] = "",
            ["share_id"] = link.ShareId,
            ["password"] = link.Password ?? "",
            ["path"] = folders[0],
            ["page"] = 1,
            ["limit"] = 50,
            ["sort_type"] = 1,
            ["as_dir"] = false,
            ["reverse"] = false,
            ["file_exts"] = Array.Empty<string>()
        });
        using var listed = await SendJsonAsync(
            HttpMethod.Post,
            $"{deviceBase}/ugreen/v1/filemgr/getShearDirFileList",
            body,
            cookie,
            ct).ConfigureAwait(false);
        var data = RequireData(listed.Document);
        return PickVideos(ArrayProp(data, "files"));
    }

    private static List<ListedFile> PickVideos(JsonElement files)
    {
        var list = new List<ListedFile>();
        if (files.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || Int(file, "file_type") != 0)
                continue;
            var path = Str(file, "path");
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var ext = Str(file, "ext") ?? "";
            var collation = Str(file, "file_collation") ?? "";
            if (!collation.Equals("video", StringComparison.OrdinalIgnoreCase) && !VideoExtensions.Contains(ext))
                continue;
            var name = Str(file, "name");
            list.Add(new ListedFile(string.IsNullOrWhiteSpace(name) ? "video" : name, path));
        }

        return list;
    }

    private static List<string> PickFolders(JsonElement files)
    {
        var list = new List<string>();
        if (files.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || Int(file, "file_type") != 1)
                continue;
            var path = Str(file, "path");
            if (!string.IsNullOrWhiteSpace(path))
                list.Add(path);
        }

        return list;
    }

    private static JsonElement ArrayProp(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var arr)
            ? arr
            : default;

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        string url,
        string? cookie,
        bool deviceHeaders,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyDeviceHeaders(request, cookie, deviceHeaders);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UgreenShareException(VideoErrorCode.NetReset, $"绿联接口 HTTP {(int)response.StatusCode}");
        return JsonDocument.Parse(text);
    }

    private static async Task<JsonReply> SendJsonAsync(
        HttpMethod method,
        string url,
        string json,
        string? cookie,
        CancellationToken ct,
        bool portal = false)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (portal)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://www.ug.link");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.ug.link/");
            request.Headers.TryAddWithoutValidation("lang", "zh-CN");
        }
        else
        {
            ApplyDeviceHeaders(request, cookie, deviceHeaders: true);
        }

        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UgreenShareException(VideoErrorCode.NetReset, $"绿联接口 HTTP {(int)response.StatusCode}");
        string? setCookie = null;
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            setCookie = string.Join("\n", values);
        return new JsonReply(JsonDocument.Parse(text), setCookie);
    }

    private static void ApplyDeviceHeaders(HttpRequestMessage request, string? cookie, bool deviceHeaders)
    {
        if (!deviceHeaders)
            return;
        request.Headers.TryAddWithoutValidation("UG-Agent", "PC/WEB");
        request.Headers.TryAddWithoutValidation("X-Specify-Language", "zh-CN");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private static string ReadShareCookie(string? setCookie, string shareId)
    {
        if (string.IsNullOrWhiteSpace(setCookie))
            throw new UgreenShareException(VideoErrorCode.AuthForbidden, "绿联分享没有返回播放凭证");

        var prefix = "share_cookie_" + shareId + "=";
        foreach (var line in setCookie.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split(';', 2)[0].Trim();
            if (pair.StartsWith(prefix, StringComparison.Ordinal))
                return pair;
        }

        throw new UgreenShareException(VideoErrorCode.AuthForbidden, "绿联分享没有返回播放凭证");
    }

    private static JsonElement RequireData(JsonDocument doc)
    {
        var root = doc.RootElement;
        var code = Int(root, "code", -1);
        if (code != 200)
        {
            var msg = Str(root, "msg");
            throw new UgreenShareException(
                VideoErrorCode.SourceUnsupported,
                string.IsNullOrWhiteSpace(msg) ? "绿联分享接口拒绝了请求" : msg);
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return data;
        return root;
    }

    private static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement obj, string name, int fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
    }

    private static bool Bool(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();
}
