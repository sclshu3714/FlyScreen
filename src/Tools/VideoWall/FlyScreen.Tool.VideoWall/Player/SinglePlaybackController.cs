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
    private float _rate = 1f;
    private bool _loop;

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

    public async Task PlaySourceAsync(
        string sourceId,
        IntPtr? renderTarget = null,
        IVideoFrameSink? frameSink = null,
        CancellationToken ct = default)
    {
        var source = _catalog.Sources.FirstOrDefault(s => s.Id == sourceId)
                     ?? throw new InvalidOperationException($"源不存在：{sourceId}");

        // 软件帧与 HWND 互斥：有 frameSink 时不绑 RenderTarget
        await PlayEndpointAsync(source.Endpoint, sourceId, new MediaPlayerOptions
        {
            DecodePreference = source.DecodePreference,
            BufferMs = source.Endpoint.BufferMs,
            Mute = false,
            RenderTarget = frameSink is null ? renderTarget : null
        }, ct, frameSink).ConfigureAwait(false);
    }

    public async Task PlayEndpointAsync(
        VideoEndpoint endpoint,
        string? sourceId,
        MediaPlayerOptions? options = null,
        CancellationToken ct = default,
        IVideoFrameSink? frameSink = null)
    {
        await StopAsync().ConfigureAwait(false);

        options ??= new MediaPlayerOptions { Mute = false };
        if (frameSink is not null)
            options = options with { RenderTarget = null };

        LastStats = null;
        _endpoint = endpoint;
        lock (_gate)
            _sourceId = sourceId;

        if (!_backend.IsAvailable)
        {
            FlyScreen.Services.AppLog.Warn("视频墙：后端不可用，无法开播");
            StateChanged?.Invoke(TilePlayState.Failed);
            await UpdateValidationAsync(sourceId, false).ConfigureAwait(false);
            return;
        }

        FlyScreen.Services.AppLog.Info(
            $"视频墙：开播 source={sourceId ?? "(temp)"} proto={endpoint.Protocol} sink={(frameSink is null ? "hwnd/none" : "frame")}");
        _player = _backend.CreatePlayer(endpoint, options);
        if (frameSink is not null)
            _player.AttachFrameSink(frameSink);

        lock (_gate)
        {
            _muted = options.Mute;
            if (_muted && _volume > 0)
                _volumeBeforeMute = _volume;
        }

        ApplyVolumeToPlayer();
        float rate;
        lock (_gate) rate = _rate;
        _player.SetRate(rate);
        bool loop;
        lock (_gate) loop = _loop;
        _player.SetLoop(loop);
        _player.StateChanged += OnStateChanged;
        _player.StatsChanged += s =>
        {
            LastStats = s;
            StatsChanged?.Invoke(s);
        };

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
            return PlaySourceAsync(sourceId, renderTarget: null, frameSink: null, ct);

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

    /// <summary>播放结束后是否循环（跨换源保持）。</summary>
    public bool Loop
    {
        get { lock (_gate) return _loop; }
        set => SetLoop(value);
    }

    public void SetLoop(bool loop)
    {
        lock (_gate) _loop = loop;
        _player?.SetLoop(loop);
    }

    /// <summary>最近一次上报的统计（分辨率/帧率），无播放时为 null。</summary>
    public MediaStats? LastStats { get; private set; }

    /// <summary>当前播放源的关键事件标记（白点）。</summary>
    public IReadOnlyList<PlaybackCue> GetKeyEvents()
    {
        string? id;
        lock (_gate) id = _sourceId;
        if (string.IsNullOrWhiteSpace(id)) return [];
        return _catalog.Sources.FirstOrDefault(s => s.Id == id)?.KeyEvents ?? [];
    }

    /// <summary>覆盖当前源的关键事件标记并落盘；无固定源时返回 false。</summary>
    public async Task<bool> SetKeyEventsAsync(IEnumerable<PlaybackCue> cues)
    {
        string? id;
        lock (_gate) id = _sourceId;
        if (string.IsNullOrWhiteSpace(id)) return false;

        var source = _catalog.Sources.FirstOrDefault(s => s.Id == id);
        if (source is null) return false;

        var list = cues.Where(c => c.PositionMs >= 0).OrderBy(c => c.PositionMs).ToList();
        var result = _catalog.Update(source with { KeyEvents = list });
        if (!result.Added) return false;

        await _saveCatalogAsync().ConfigureAwait(false);
        return true;
    }

    public float Rate
    {
        get { lock (_gate) return _rate; }
    }

    public void SetRate(float rate)
    {
        var r = Math.Clamp(rate, 0.25f, 4f);
        lock (_gate) _rate = r;
        _player?.SetRate(r);
    }

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