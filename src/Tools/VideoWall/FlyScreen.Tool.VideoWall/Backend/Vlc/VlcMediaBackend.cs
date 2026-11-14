using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using LibVLCSharp.Shared;

namespace FlyScreen.Tool.VideoWall.Backend.Vlc;

/// <summary>LibVLC 播放后端：单 <see cref="LibVLC"/> 实例、多 <see cref="MediaPlayer"/>。</summary>
public sealed class VlcMediaBackend : IMediaBackend, IDisposable
{
    private static readonly TimeSpan CoverTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly CredentialStore? _credentials;
    private readonly object _gate = new();
    private LibVLC? _libVlc;
    private bool _disposed;

    public VlcMediaBackend(CredentialStore? credentials = null)
    {
        _credentials = credentials;
        IsAvailable = VlcNativeBootstrap.TryEnsureInitialized();
    }

    public string Name => "vlc";

    public bool IsAvailable { get; }

    public IMediaPlayer CreatePlayer(VideoEndpoint endpoint, MediaPlayerOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
            throw new InvalidOperationException("LibVLC 不可用");

        var libVlc = GetOrCreateLibVlc();
        return new VlcMediaPlayer(libVlc, endpoint, options, _credentials);
    }

    public async Task<MediaProbeResult> ProbeAsync(VideoEndpoint endpoint, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return new MediaProbeResult
            {
                Ok = false,
                Error = VideoErrorCode.SourceUnsupported,
                Message = "LibVLC 未安装或原生库缺失"
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        return await Task.Run(() => ProbeCore(endpoint, timeout.Token), timeout.Token).ConfigureAwait(false);
    }

    public async Task<CoverCaptureResult> CaptureCoverAsync(VideoEndpoint endpoint, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return new CoverCaptureResult
            {
                Ok = false,
                Error = VideoErrorCode.SourceUnsupported,
                Message = "LibVLC 未安装或原生库缺失"
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CoverTimeout);

        return await Task.Run(() => CaptureCore(endpoint, timeout.Token), timeout.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _libVlc?.Dispose();
            _libVlc = null;
        }
    }

    private LibVLC GetOrCreateLibVlc()
    {
        lock (_gate)
        {
            if (_libVlc is not null)
                return _libVlc;

            _libVlc = new LibVLC(
                "--no-video-title-show",
                "--no-stats",
                "--verbose=0");
            return _libVlc;
        }
    }

    private MediaProbeResult ProbeCore(VideoEndpoint endpoint, CancellationToken ct)
    {
        var libVlc = GetOrCreateLibVlc();
        using var media = VlcMrlHelper.CreateMedia(libVlc, endpoint, new MediaPlayerOptions
        {
            DecodePreference = "auto",
            BufferMs = endpoint.BufferMs
        }, _credentials);

        try
        {
            media.Parse(MediaParseOptions.ParseNetwork);
        }
        catch (Exception ex)
        {
            return new MediaProbeResult
            {
                Ok = false,
                Error = VlcErrorMapper.FromMessage(ex.Message),
                Message = ex.Message
            };
        }

        ct.ThrowIfCancellationRequested();

        if (media.ParsedStatus == MediaParsedStatus.Failed)
        {
            var msg = media.Meta(MetadataType.Title) ?? "解析失败";
            return new MediaProbeResult
            {
                Ok = false,
                Error = VlcErrorMapper.FromMessage(msg),
                Message = msg
            };
        }

        VlcTrackInfo.TryGetVideo(media, out var video);
        return new MediaProbeResult
        {
            Ok = true,
            Message = "解析成功",
            VideoCodec = video.Codec,
            Width = video.Width > 0 ? video.Width : null,
            Height = video.Height > 0 ? video.Height : null,
            Fps = video.Fps > 0 ? video.Fps : null
        };
    }

    private CoverCaptureResult CaptureCore(VideoEndpoint endpoint, CancellationToken ct)
    {
        var libVlc = GetOrCreateLibVlc();
        using var player = new MediaPlayer(libVlc);
        using var media = VlcMrlHelper.CreateMedia(libVlc, endpoint, new MediaPlayerOptions
        {
            DecodePreference = "auto",
            Mute = true
        }, _credentials);

        media.AddOption(":vout=dummy");
        media.AddOption(":no-audio");
        player.Media = media;

        var temp = Path.Combine(Path.GetTempPath(), $"videowall-cover-{Guid.NewGuid():N}.jpg");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = false;

        void OnPlaying(object? _, EventArgs __)
        {
            try
            {
                if (player.TakeSnapshot(0, temp, 320, 180))
                    tcs.TrySetResult(true);
            }
            catch
            {
                failed = true;
                tcs.TrySetResult(false);
            }
        }

        void OnError(object? _, EventArgs __)
        {
            failed = true;
            tcs.TrySetResult(false);
        }

        player.Playing += OnPlaying;
        player.EncounteredError += OnError;

        try
        {
            player.Play();
            var completed = tcs.Task.Wait(CoverTimeout);
            if (!completed || failed || !File.Exists(temp))
            {
                return new CoverCaptureResult
                {
                    Ok = false,
                    Error = VideoErrorCode.NetTimeout,
                    Message = "抓帧超时或失败"
                };
            }

            ct.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(temp);
            VlcTrackInfo.TryGetVideo(media, out var video);

            // P0-a：后端可返回任意尺寸的 JPEG，CoverCaptureService/UI 可再 letterbox
            return new CoverCaptureResult
            {
                Ok = true,
                JpegBytes = bytes,
                Width = 320,
                Height = 180,
                VideoCodec = video.Codec,
                Fps = video.Fps
            };
        }
        finally
        {
            player.Playing -= OnPlaying;
            player.EncounteredError -= OnError;
            player.Stop();
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch { /* ignore */ }
        }
    }
}