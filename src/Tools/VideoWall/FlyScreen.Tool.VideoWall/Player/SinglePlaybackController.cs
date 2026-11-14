using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;

namespace FlyScreen.Tool.VideoWall.Player;

/// <summary>源管理单条播放控制器。</summary>
public sealed class SinglePlaybackController : IDisposable
{
    private readonly IMediaBackend _backend;
    private readonly VideoSourceCatalog _catalog;
    private readonly Func<Task> _saveCatalogAsync;
    private readonly object _gate = new();

    private IMediaPlayer? _player;
    private string? _sourceId;
    private VideoEndpoint? _endpoint;
    private bool _muted;
    private int _volume = 100;
    private int _volumeBeforeMute = 100;

    public SinglePlaybackController(
        IMediaBackend backend,
        VideoSourceCatalog catalog,
        Func<Task> saveCatalogAsync)
    {
        _backend = backend;
        _catalog = catalog;
        _saveCatalogAsync = saveCatalogAsync;
    }

    public TilePlayState State => _player?.State ?? TilePlayState.Idle;

    public VideoErrorCode ErrorCode => _player?.ErrorCode ?? VideoErrorCode.None;

    public long TimeMs => _player?.TimeMs ?? 0;

    public long LengthMs => _player?.LengthMs ?? 0;

    public bool CanSeek => _player?.CanSeek ?? false;

    public bool IsMuted
    {
        get { lock (_gate) return _muted || _volume <= 0; }
    }

    /// <summary>当前生效音量 0–100（静音时为 0）。</summary>
    public int Volume
    {
        get
        {
            lock (_gate)
                return _muted || _volume <= 0 ? 0 : _volume;
        }
    }

    /// <summary>取消静音时将恢复的音量（至少为 1）。</summary>
    public int VolumeBeforeMute
    {
        get
        {
            lock (_gate)
                return _volumeBeforeMute > 0 ? _volumeBeforeMute : 100;
        }
    }

    public string? CurrentSourceId
    {
        get { lock (_gate) return _sourceId; }
    }

    public event Action<TilePlayState>? StateChanged;
    public event Action<MediaStats>? StatsChanged;

    public async Task PlaySourceAsync(string sourceId, IntPtr? renderTarget = null, CancellationToken ct = default)
    {
        var source = _catalog.Sources.FirstOrDefault(s => s.Id == sourceId)
                     ?? throw new InvalidOperationException($"源不存在：{sourceId}");

        await PlayEndpointAsync(source.Endpoint, sourceId, new MediaPlayerOptions
        {
            DecodePreference = source.DecodePreference,
            BufferMs = source.Endpoint.BufferMs,
            Mute = false,
            RenderTarget = renderTarget
        }, ct).ConfigureAwait(false);
    }

    public async Task PlayEndpointAsync(
        VideoEndpoint endpoint,
        string? sourceId,
        MediaPlayerOptions? options = null,
        CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);

        options ??= new MediaPlayerOptions { Mute = false };
        _endpoint = endpoint;
        lock (_gate)
            _sourceId = sourceId;

        if (!_backend.IsAvailable)
        {
            StateChanged?.Invoke(TilePlayState.Failed);
            await UpdateValidationAsync(sourceId, false).ConfigureAwait(false);
            return;
        }

        _player = _backend.CreatePlayer(endpoint, options);
        lock (_gate)
        {
            _muted = options.Mute;
            if (_muted && _volume > 0)
                _volumeBeforeMute = _volume;
        }

        ApplyVolumeToPlayer();
        _player.StateChanged += OnStateChanged;
        _player.StatsChanged += s => StatsChanged?.Invoke(s);

        await _player.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_player is null) return;

        _player.StateChanged -= OnStateChanged;
        await _player.StopAsync().ConfigureAwait(false);
        if (_player is IDisposable disposable)
            disposable.Dispose();
        _player = null;
        StateChanged?.Invoke(TilePlayState.Stopped);
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (_endpoint is null) return;
        await PlayEndpointAsync(_endpoint, _sourceId, new MediaPlayerOptions { Mute = _muted }, ct).ConfigureAwait(false);
    }

    public void Pause() => _player?.Pause();

    public void Resume() => _player?.Resume();

    public void TogglePause()
    {
        if (_player is null) return;
        if (_player.State == TilePlayState.Paused)
            _player.Resume();
        else
            _player.Pause();
    }

    /// <summary>停止并释放播放器后，用上次源/端点重新开播。</summary>
    public Task ReplayAsync(CancellationToken ct = default)
    {
        string? sourceId;
        VideoEndpoint? endpoint;
        bool muted;
        lock (_gate)
        {
            sourceId = _sourceId;
            endpoint = _endpoint;
            muted = _muted;
        }

        if (!string.IsNullOrWhiteSpace(sourceId))
            return PlaySourceAsync(sourceId, renderTarget: null, ct);

        if (endpoint is null)
            return Task.CompletedTask;

        return PlayEndpointAsync(endpoint, sourceId, new MediaPlayerOptions { Mute = muted }, ct);
    }

    public void SeekTo(long timeMs) => _player?.SeekTo(timeMs);

    public void SeekBy(long deltaMs) => _player?.SeekBy(deltaMs);

    public void ToggleMute()
    {
        lock (_gate)
        {
            if (_muted || _volume <= 0)
            {
                _muted = false;
                if (_volume <= 0)
                    _volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 100;
            }
            else
            {
                _volumeBeforeMute = _volume > 0 ? _volume : 100;
                _muted = true;
            }
        }

        ApplyVolumeToPlayer();
    }

    public void SetVolume(int volume0To100)
    {
        var v = Math.Clamp(volume0To100, 0, 100);
        lock (_gate)
        {
            _volume = v;
            if (v <= 0)
            {
                _muted = true;
            }
            else
            {
                _muted = false;
                _volumeBeforeMute = v;
            }
        }

        ApplyVolumeToPlayer();
    }

    public void SetRenderTarget(IntPtr target) => _player?.SetRenderTarget(target);

    private void ApplyVolumeToPlayer()
    {
        if (_player is null) return;
        int vol;
        bool muted;
        lock (_gate)
        {
            muted = _muted || _volume <= 0;
            vol = muted ? 0 : _volume;
        }

        _player.SetMuted(muted);
        _player.SetVolume(vol);
    }

    /// <summary>
    /// 底层原生播放引擎（LibVLC <c>MediaPlayer</c>），供 WPF VideoView 绑定。
    /// </summary>
    public object? NativePlaybackEngine
        => _player is Backend.Vlc.VlcMediaPlayer vlc ? vlc.NativeMediaPlayer : null;

    public void Dispose() => _ = StopAsync();

    private async void OnStateChanged(TilePlayState state)
    {
        StateChanged?.Invoke(state);

        if (state is TilePlayState.Playing)
            await UpdateValidationAsync(_sourceId, true).ConfigureAwait(false);
        else if (state is TilePlayState.Failed)
            await UpdateValidationAsync(_sourceId, false).ConfigureAwait(false);
    }

    private async Task UpdateValidationAsync(string? sourceId, bool ok)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        _catalog.UpdateValidation(
            sourceId,
            ok ? SourceValidationState.Ok : SourceValidationState.Failed,
            lastPlayedAt: DateTimeOffset.Now);

        await _saveCatalogAsync().ConfigureAwait(false);
    }
}