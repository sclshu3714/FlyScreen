using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Backend;

/// <summary>LibVLC 不可用时的占位后端。</summary>
public sealed class NullMediaBackend : IMediaBackend
{
    public string Name => "null";

    public bool IsAvailable => false;

    public IMediaPlayer CreatePlayer(VideoEndpoint endpoint, MediaPlayerOptions options)
        => new NullMediaPlayer();

    public Task<MediaProbeResult> ProbeAsync(VideoEndpoint endpoint, CancellationToken ct = default)
        => Task.FromResult(new MediaProbeResult
        {
            Ok = false,
            Error = VideoErrorCode.SourceUnsupported,
            Message = "播放后端不可用（LibVLC 未安装）",
            NeedsBackend = true
        });

    public Task<CoverCaptureResult> CaptureCoverAsync(VideoEndpoint endpoint, CancellationToken ct = default)
        => Task.FromResult(new CoverCaptureResult
        {
            Ok = false,
            Error = VideoErrorCode.SourceUnsupported,
            Message = "播放后端不可用（LibVLC 未安装）"
        });
}

internal sealed class NullMediaPlayer : IMediaPlayer, IDisposable
{
    public TilePlayState State { get; private set; } = TilePlayState.Idle;
    public VideoErrorCode ErrorCode { get; private set; } = VideoErrorCode.None;

    public event Action<TilePlayState>? StateChanged;
    public event Action<MediaStats>? StatsChanged;

    public void SetRenderTarget(IntPtr target) { }

    public Task StartAsync(CancellationToken ct = default)
    {
        ErrorCode = VideoErrorCode.SourceUnsupported;
        SetState(TilePlayState.Failed);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        SetState(TilePlayState.Stopped);
        return Task.CompletedTask;
    }

    public void SetVolume(int volume0To100) { }
    public void SetMuted(bool muted) { }
    public void Dispose() { }

    private void SetState(TilePlayState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}