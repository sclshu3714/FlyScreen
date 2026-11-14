using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>
/// 轻量连通性探测（不依赖 LibVLC、不建播放会话）。
/// RTSP/RTMP/SRT 仅做 TCP 可达性或返回「需完整后端」软结果（docs §7）。
/// </summary>
public sealed class ProbeService
{
    private const int FileTimeoutMs = 1000;
    private const int HttpTimeoutMs = 3000;
    private const int StreamTimeoutMs = 3000;
    private const int FileHeadBytes = 1024 * 1024;

    /// <summary>对端点做轻量探测。</summary>
    public async Task<MediaProbeResult> ProbeAsync(VideoEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var protocol = (endpoint.Protocol ?? "").Trim().ToLowerInvariant();
        return protocol switch
        {
            "file" => await ProbeFileAsync(endpoint.Address, ct).ConfigureAwait(false),
            "http" or "https" or "hls" or "dash" => await ProbeHttpAsync(endpoint, ct).ConfigureAwait(false),
            "rtsp" or "rtmp" or "rtmps" or "srt" => await ProbeStreamSoftAsync(endpoint, protocol, ct).ConfigureAwait(false),
            "udp" or "rtp" => SoftNeedsBackend(protocol, "组播/裸 RTP 需播放后端解码首帧"),
            _ => new MediaProbeResult
            {
                Ok = false,
                Error = VideoErrorCode.SourceUnsupported,
                Message = $"暂不支持轻量探测协议：{protocol}"
            }
        };
    }

    private static async Task<MediaProbeResult> ProbeFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail(VideoErrorCode.IoDisk, "文件路径为空");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FileTimeoutMs);

            if (!File.Exists(path))
                return Fail(VideoErrorCode.SourceNotFound, "文件不存在或不可访问");

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);

            var buffer = new byte[Math.Min(FileHeadBytes, stream.Length > 0 ? (int)Math.Min(stream.Length, FileHeadBytes) : FileHeadBytes)];
            _ = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);

            var ext = Path.GetExtension(path);
            return new MediaProbeResult
            {
                Ok = true,
                Message = "文件可读",
                Container = string.IsNullOrEmpty(ext) ? null : ext.TrimStart('.').ToLowerInvariant()
            };
        }
        catch (OperationCanceledException)
        {
            return Fail(VideoErrorCode.NetTimeout, "读取文件超时");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(VideoErrorCode.AuthForbidden, "没有读取该文件的权限");
        }
        catch (IOException ex)
        {
            return Fail(VideoErrorCode.IoDisk, ex.Message);
        }
    }

    private async Task<MediaProbeResult> ProbeHttpAsync(VideoEndpoint endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Address))
            return Fail(VideoErrorCode.SourceUnsupported, "地址为空");

        if (!Uri.TryCreate(endpoint.Address, UriKind.Absolute, out var uri))
            return Fail(VideoErrorCode.SourceUnsupported, "HTTP(S) 地址格式无效");

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs)
        };

        foreach (var (key, value) in endpoint.Headers)
            client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed || response.StatusCode == HttpStatusCode.NotImplemented)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                get.Headers.Range = new RangeHeaderValue(0, 65535);
                using var rangeResponse = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                return MapHttpResponse(rangeResponse, endpoint.Protocol);
            }

            return MapHttpResponse(response, endpoint.Protocol);
        }
        catch (TaskCanceledException)
        {
            return Fail(VideoErrorCode.NetTimeout, "HTTP 请求超时");
        }
        catch (HttpRequestException ex)
        {
            return Fail(MapHttpException(ex), ex.Message);
        }
    }

    private static MediaProbeResult MapHttpResponse(HttpResponseMessage response, string protocol)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return Fail(VideoErrorCode.AuthRequired, "需要认证（401）");

        if (response.StatusCode == HttpStatusCode.Forbidden)
            return Fail(VideoErrorCode.AuthForbidden, "访问被拒绝（403）");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return Fail(VideoErrorCode.SourceNotFound, "资源不存在（404）");

        if (!response.IsSuccessStatusCode)
            return Fail(VideoErrorCode.SourceUnsupported, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var contentType = response.Content.Headers.ContentType?.MediaType;
        return new MediaProbeResult
        {
            Ok = true,
            Message = "HTTP 可达",
            Container = protocol is "hls" ? "hls" : protocol is "dash" ? "dash" : contentType
        };
    }

    private static VideoErrorCode MapHttpException(HttpRequestException ex)
    {
        if (ex.StatusCode == HttpStatusCode.NotFound) return VideoErrorCode.SourceNotFound;
        if (ex.StatusCode == HttpStatusCode.Unauthorized) return VideoErrorCode.AuthRequired;
        if (ex.StatusCode == HttpStatusCode.Forbidden) return VideoErrorCode.AuthForbidden;
        return VideoErrorCode.NetReset;
    }

    private async Task<MediaProbeResult> ProbeStreamSoftAsync(VideoEndpoint endpoint, string protocol, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint.Address, UriKind.Absolute, out var uri))
            return Fail(VideoErrorCode.SourceUnsupported, "流地址格式无效");

        var port = uri.Port;
        if (port <= 0)
        {
            port = protocol switch
            {
                "rtsp" => 554,
                "rtmp" => 1935,
                "rtmps" => 443,
                "srt" => 9000,
                _ => 554
            };
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StreamTimeoutMs);

            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, port, timeout.Token).ConfigureAwait(false);

            return new MediaProbeResult
            {
                Ok = true,
                NeedsBackend = true,
                Message = $"{protocol.ToUpperInvariant()} 端口可达；编码与分辨率需播放后端探测（DESCRIBE/握手）"
            };
        }
        catch (OperationCanceledException)
        {
            return Fail(VideoErrorCode.NetTimeout, "连接超时");
        }
        catch (SocketException ex)
        {
            return Fail(MapSocketError(ex.SocketErrorCode), ex.Message);
        }
    }

    private static MediaProbeResult SoftNeedsBackend(string protocol, string hint)
        => new()
        {
            Ok = true,
            NeedsBackend = true,
            Message = $"{protocol}：{hint}"
        };

    private static VideoErrorCode MapSocketError(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData => VideoErrorCode.NetDns,
        SocketError.TimedOut => VideoErrorCode.NetTimeout,
        SocketError.ConnectionRefused or SocketError.NetworkUnreachable => VideoErrorCode.NetReset,
        _ => VideoErrorCode.NetReset
    };

    private static MediaProbeResult Fail(VideoErrorCode code, string message)
        => new() { Ok = false, Error = code, Message = message };
}