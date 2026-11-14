using FlyScreen.Tool.VideoWall.Backend.Vlc;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary>单格播放会话：独立状态机与重连。</summary>
public sealed class TileSession : IDisposable
{
    private readonly IMediaBackend _backend;
    private readonly ReconnectScheduler _reconnect = new();
    private readonly object _gate = new();

    private IMediaPlayer? _player;
    private VideoEndpoint? _endpoint;
    private TilePlayState _state = TilePlayState.Idle;
    private bool _muted = true;
    private IntPtr _renderTarget;
    private CancellationTokenSource? _reconnectCts;

    public TileSession(int index, IMediaBackend backend)
    {
        Index = index;
        _backend = backend;
    }

    public int Index { get; }

    public string? SourceId { get; private set; }

    public string? TemporaryAddress { get; private set; }

    public TilePlayState State
    {
        get { lock (_gate) return _state; }
    }

    public VideoErrorCode ErrorCode => _player?.ErrorCode ?? VideoErrorCode.None;

    public MediaStats? LastStats { get; private set; }

    public event Action<TileSession>? Changed;

    public async Task BindAsync(
        VideoEndpoint endpoint,
        string? sourceId,
        string? temporaryAddress,
        MediaPlayerOptions options,
        CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);

        SourceId = sourceId;
        TemporaryAddress = temporaryAddress;
        _endpoint = endpoint;
        _muted = options.Mute;
        _renderTarget = options.RenderTarget ?? IntPtr.Zero;

        if (!_backend.IsAvailable)
        {
            SetSessionState(TilePlayState.Failed);
            return;
        }

        _reconnect.Reset();
        _player = _backend.CreatePlayer(endpoint, options with { Mute = _muted, RenderTarget = _renderTarget });
        WirePlayer(_player);

        SetSessionState(TilePlayState.Resolving);
        await _player.StartAsync(ct).ConfigureAwait(false);
    }

    public void SetRenderTarget(IntPtr target)
    {
        _renderTarget = target;
        _player?.SetRenderTarget(target);
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _player?.SetMuted(muted);
    }

    public async Task StopAsync()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;

        if (_player is not null)
        {
            UnwirePlayer(_player);
            await _player.StopAsync().ConfigureAwait(false);
            if (_player is IDisposable disposable)
                disposable.Dispose();
            _player = null;
        }

        SourceId = null;
        TemporaryAddress = null;
        _endpoint = null;
        SetSessionState(TilePlayState.Stopped);
    }

    public Task ReconnectAsync(CancellationToken ct = default)
    {
        _reconnect.Reset();
        return RestartPlaybackAsync(ct);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private void WirePlayer(IMediaPlayer player)
    {
        player.StateChanged += OnPlayerStateChanged;
        player.StatsChanged += stats =>
        {
            LastStats = stats;
            Changed?.Invoke(this);
        };
    }

    private void UnwirePlayer(IMediaPlayer player)
    {
        player.StateChanged -= OnPlayerStateChanged;
    }

    private void OnPlayerStateChanged(TilePlayState playerState)
    {
        if (playerState == TilePlayState.Failed && VlcErrorMapper.IsNonRetryable(_player?.ErrorCode ?? VideoErrorCode.None))
        {
            SetSessionState(TilePlayState.Failed);
            return;
        }

        if (playerState is TilePlayState.Stalled or TilePlayState.Failed)
        {
            _ = ScheduleReconnectAsync();
            return;
        }

        if (playerState == TilePlayState.Reconnecting)
        {
            SetSessionState(TilePlayState.Reconnecting);
            return;
        }

        SetSessionState(playerState);
    }

    private async Task ScheduleReconnectAsync()
    {
        if (_endpoint is null || !_reconnect.CanRetry)
        {
            SetSessionState(TilePlayState.Failed);
            return;
        }

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;

        SetSessionState(TilePlayState.Reconnecting);

        var delay = _reconnect.GetNextDelay();
        _reconnect.RecordAttempt();

        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            await RestartPlaybackAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被 Stop/换源取消
        }
    }

    private async Task RestartPlaybackAsync(CancellationToken ct)
    {
        if (_endpoint is null || !_backend.IsAvailable)
        {
            SetSessionState(TilePlayState.Failed);
            return;
        }

        if (_player is not null)
        {
            await _player.StopAsync().ConfigureAwait(false);
            if (_player is IDisposable disposable)
                disposable.Dispose();
        }

        _player = _backend.CreatePlayer(_endpoint, new MediaPlayerOptions
        {
            Mute = _muted,
            RenderTarget = _renderTarget
        });
        WirePlayer(_player);
        SetSessionState(TilePlayState.Connecting);
        await _player.StartAsync(ct).ConfigureAwait(false);
    }

    private void SetSessionState(TilePlayState state)
    {
        bool raise;
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
            raise = true;
        }

        if (raise)
            Changed?.Invoke(this);
    }
}