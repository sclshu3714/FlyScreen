using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using FlyScreen.Services;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>
/// 用自有 HTTP 客户端拉流再交给 LibVLC 解复用。
/// LibVLC 3 的 HTTPS 模块只认 <c>http-referrer</c> / <c>http-user-agent</c>，
/// 发不出 Cookie、<c>UG-Agent</c> 这类自定义头。
/// </summary>
internal sealed class RangeHttpMediaInput : MediaInput
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly Uri _uri;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly object _gate = new();
    private HttpResponseMessage? _response;
    private Stream? _stream;
    private ulong _size;
    private ulong _position;
    private int _closed;

    public RangeHttpMediaInput(Uri uri, IReadOnlyDictionary<string, string> headers)
    {
        _uri = uri;
        _headers = headers;
    }

    public override bool Open(out ulong size)
    {
        size = 0;
        try
        {
            if (!OpenWindow(0, out var total))
                return false;
            _size = total;
            size = total;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"视频墙：拉流失败 {ex.Message}");
            Close();
            return false;
        }
    }

    public override int Read(IntPtr buf, uint len)
    {
        if (buf == IntPtr.Zero || len == 0 || _closed != 0)
            return -1;

        Stream? stream;
        lock (_gate)
            stream = _stream;
        if (stream is null)
            return -1;

        var want = (int)Math.Min(len, 256 * 1024);
        var rented = ArrayPool<byte>.Shared.Rent(want);
        try
        {
            var n = stream.Read(rented, 0, want);
            if (n <= 0)
                return 0;
            System.Runtime.InteropServices.Marshal.Copy(rented, 0, buf, n);
            _position += (ulong)n;
            return n;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpRequestException)
        {
            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override bool Seek(ulong offset)
    {
        if (_closed != 0)
            return false;
        if (_size > 0 && offset > _size)
            return false;
        if (offset == _position && _stream is not null)
            return true;

        try
        {
            return OpenWindow(offset, out _);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            return false;
        }
    }

    public override void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        lock (_gate)
            ReleaseWindow_NoLock();
    }

    private bool OpenWindow(ulong offset, out ulong total)
    {
        lock (_gate)
        {
            ReleaseWindow_NoLock();
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            request.Headers.Range = new RangeHeaderValue((long)offset, null);
            foreach (var (key, value) in _headers)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;
                request.Headers.TryAddWithoutValidation(key, value);
            }

            var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                var snippet = ReadSnippet(response);
                response.Dispose();
                AppLog.Warn($"视频墙：拉流返回的不是视频（{mediaType}）{snippet}");
                total = 0;
                return false;
            }

            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            {
                response.Dispose();
                AppLog.Warn($"视频墙：拉流 HTTP {(int)response.StatusCode}");
                total = 0;
                return false;
            }

            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                response.Dispose();
                total = 0;
                return false;
            }

            total = 0;
            var range = response.Content.Headers.ContentRange;
            if (range?.Length is long length && length >= 0)
                total = (ulong)length;
            else if (offset == 0 && response.Content.Headers.ContentLength is long contentLength && contentLength >= 0)
                total = (ulong)contentLength;

            if (total == 0)
            {
                response.Dispose();
                AppLog.Warn("视频墙：拉流响应没有文件长度");
                return false;
            }

            _response = response;
            _stream = response.Content.ReadAsStream();
            _position = offset;
            return true;
        }
    }

    private static string ReadSnippet(HttpResponseMessage response)
    {
        try
        {
            using var stream = response.Content.ReadAsStream();
            var buffer = new byte[160];
            var n = stream.Read(buffer, 0, buffer.Length);
            return n <= 0 ? "" : " " + System.Text.Encoding.UTF8.GetString(buffer, 0, n);
        }
        catch
        {
            return "";
        }
    }

    private void ReleaseWindow_NoLock()
    {
        _stream?.Dispose();
        _stream = null;
        _response?.Dispose();
        _response = null;
    }
}
