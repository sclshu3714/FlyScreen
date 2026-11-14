using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>封面抓取结果（含缓存键与元数据）。</summary>
public sealed record CoverCaptureOutcome(
    bool Ok,
    string? CoverKey,
    string? FilePath,
    VideoErrorCode Error,
    string? Message,
    int Width,
    int Height,
    string? VideoCodec,
    double Fps,
    DateTimeOffset CapturedAt);

/// <summary>
/// 首帧封面管线：调用 <see cref="IMediaBackend.CaptureCoverAsync"/>，写入 <see cref="CoverCache"/>（docs §5）。
/// 缩放由后端负责；本类只落盘 JPEG 字节。并发 ≤ 4。
/// </summary>
public sealed class CoverCaptureService
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(8);
    private readonly IMediaBackend _backend;
    private readonly CoverCache _coverCache;
    private readonly CredentialStore? _credentials;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public CoverCaptureService(IMediaBackend backend, CoverCache coverCache, CredentialStore? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(coverCache);
        _backend = backend;
        _coverCache = coverCache;
        _credentials = credentials;
    }

    /// <summary>抓取封面并写入缓存目录；不更新目录 JSON。</summary>
    public async Task<CoverCaptureOutcome> CaptureAsync(VideoEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!_backend.IsAvailable)
        {
            return new CoverCaptureOutcome(
                false,
                null,
                null,
                VideoErrorCode.SourceUnsupported,
                "播放后端不可用，无法抓帧",
                0,
                0,
                null,
                0,
                default);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CaptureTimeout);

            var resolved = ResolveEndpoint(endpoint);
            var result = await _backend.CaptureCoverAsync(resolved, timeout.Token).ConfigureAwait(false);
            if (!result.Ok || result.JpegBytes is not { Length: > 0 } bytes)
            {
                return new CoverCaptureOutcome(
                    false,
                    null,
                    null,
                    result.Error,
                    result.Message ?? "抓帧失败",
                    result.Width,
                    result.Height,
                    result.VideoCodec,
                    result.Fps,
                    default);
            }

            var key = CoverCache.BuildKey(resolved);
            var path = _coverCache.PathForKey(key);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            return new CoverCaptureOutcome(
                true,
                key,
                path,
                VideoErrorCode.None,
                null,
                result.Width,
                result.Height,
                result.VideoCodec,
                result.Fps,
                DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            return new CoverCaptureOutcome(
                false,
                null,
                null,
                VideoErrorCode.NetTimeout,
                "抓帧超时（8 秒）",
                0,
                0,
                null,
                0,
                default);
        }
        finally
        {
            _gate.Release();
        }
    }

    private VideoEndpoint ResolveEndpoint(VideoEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.CredentialRef) || _credentials is null)
            return endpoint;

        var cred = _credentials.Get(endpoint.CredentialRef);
        if (cred is null)
            return endpoint;

        // 凭据不写入 Address；后端从 CredentialRef 或扩展字段读取（一期由调用方保证 ref 有效）
        return endpoint;
    }
}