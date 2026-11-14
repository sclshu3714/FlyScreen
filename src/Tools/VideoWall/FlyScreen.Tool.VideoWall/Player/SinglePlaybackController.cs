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
        _muted = options.Mute;
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

    public void ToggleMute()
    {
        _muted = !_muted;
        _player?.SetMuted(_muted);
    }

    public void SetRenderTarget(IntPtr target) => _player?.SetRenderTarget(target);

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