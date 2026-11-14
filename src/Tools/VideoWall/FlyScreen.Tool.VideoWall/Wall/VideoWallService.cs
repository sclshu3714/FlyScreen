using FlyScreen.Tool.VideoWall.Backend;
using FlyScreen.Tool.VideoWall.Backend.Vlc;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Player;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall.Wall;

public sealed class VideoWallService : IVideoWallService, IDisposable
{
    private const string SourcesFile = "video-wall.sources.json";
    private const string ScenesFile = "video-wall.scenes.json";
    private const string RecentFile = "video-wall.recent.json";
    private const string CredentialsFile = "video-wall.credentials.bin";

    private readonly object _gate = new();
    private IToolHostServices? _host;
    private string? _dataDir;
    private bool _running;
    private List<TileSession> _tiles = [];
    private WallLayout _layout = WallLayout.ThreeByThree;
    private IMediaBackend? _backend;

    public VideoWallService()
    {
        Catalog = new VideoSourceCatalog("");
        Covers = new CoverCache("");
        Credentials = new CredentialStore("");
        Scenes = new SceneStore("");
        Recent = new RecentSourcesStore("");
        Probe = new ProbeService();
        Audio = new AudioRouter();
        Backend = new NullMediaBackend();
        SinglePlayback = new SinglePlaybackController(Backend, Catalog, SaveCatalogAsync);
        CoverCapture = new CoverCaptureService(Backend, Covers, Credentials);
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public event Action? SessionChanged;
    public event Action? CatalogChanged;

    public VideoSourceCatalog Catalog { get; private set; }
    public CoverCache Covers { get; private set; }
    public CoverCaptureService CoverCapture { get; private set; }
    public ProbeService Probe { get; }
    public SceneStore Scenes { get; private set; }
    public RecentSourcesStore Recent { get; private set; }
    public CredentialStore Credentials { get; private set; }

    public IMediaBackend Backend { get; private set; }

    public WallLayout Layout
    {
        get { lock (_gate) return _layout; }
    }

    public IReadOnlyList<TileSession> Tiles
    {
        get { lock (_gate) return _tiles; }
    }

    public SinglePlaybackController SinglePlayback { get; private set; }
    public AudioRouter Audio { get; }

    public void AttachHost(IToolHostServices host) => _host = host;

    public void Start()
    {
        lock (_gate)
            _running = true;
        RaiseSession();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            foreach (var tile in _tiles)
                tile.Dispose();
            _tiles.Clear();
        }

        SinglePlayback.Dispose();

        if (Backend is IDisposable d)
            d.Dispose();

        RaiseSession();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_host is null)
            throw new InvalidOperationException("宿主未附加");

        _dataDir = _host.GetToolDataDirectory(VideoWallIds.ToolId);
        Directory.CreateDirectory(_dataDir);

        Catalog = new VideoSourceCatalog(Path.Combine(_dataDir, SourcesFile));
        Covers = new CoverCache(Path.Combine(_dataDir, "covers"));
        Credentials = new CredentialStore(Path.Combine(_dataDir, CredentialsFile));
        Scenes = new SceneStore(Path.Combine(_dataDir, ScenesFile));
        Recent = new RecentSourcesStore(Path.Combine(_dataDir, RecentFile));

        await Catalog.LoadAsync(ct).ConfigureAwait(false);
        await Scenes.LoadAsync(ct).ConfigureAwait(false);
        await Recent.LoadAsync(ct).ConfigureAwait(false);
        Credentials.Load();

        var vlc = new VlcMediaBackend(Credentials);
        Backend = vlc.IsAvailable ? vlc : new NullMediaBackend();

        SinglePlayback.Dispose();
        SinglePlayback = new SinglePlaybackController(Backend, Catalog, SaveCatalogAsync);
        CoverCapture = new CoverCaptureService(Backend, Covers, Credentials);

        lock (_gate)
            EnsureTiles(_layout);

        var auto = Scenes.AutoStartScene;
        if (auto is not null)
        {
            await SetLayoutAsync(auto.Layout, ct).ConfigureAwait(false);
            foreach (var binding in auto.Tiles)
            {
                if (!string.IsNullOrWhiteSpace(binding.SourceId))
                    await BindTileAsync(binding.Index, binding.SourceId, ct).ConfigureAwait(false);
            }

            Audio.Policy = auto.Audio;
            Audio.SoloTile = auto.SoloTile;
            Audio.Apply(_tiles);
        }
    }

    public async Task SetLayoutAsync(WallLayout layout, CancellationToken ct = default)
    {
        if (layout.Rows is < 1 or > 4 || layout.Columns is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(layout), "布局边长必须为 1–4");

        lock (_gate)
        {
            foreach (var tile in _tiles)
                tile.Dispose();

            _layout = layout;
            EnsureTiles(layout);
        }

        await Task.CompletedTask;
        RaiseSession();
    }

    public async Task BindTileAsync(int index, string? sourceId, CancellationToken ct = default)
    {
        var tile = GetTile(index);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            await tile.StopAsync().ConfigureAwait(false);
            return;
        }

        var source = Catalog.Sources.FirstOrDefault(s => s.Id == sourceId)
                     ?? throw new InvalidOperationException($"源不存在：{sourceId}");

        await tile.BindAsync(
            source.Endpoint,
            sourceId,
            null,
            new MediaPlayerOptions
            {
                DecodePreference = source.DecodePreference,
                BufferMs = source.Endpoint.BufferMs,
                Mute = true
            },
            ct).ConfigureAwait(false);

        RaiseSession();
    }

    public async Task SwapTilesAsync(int a, int b, CancellationToken ct = default)
    {
        var tileA = GetTile(a);
        var tileB = GetTile(b);

        var aSource = tileA.SourceId;
        var bSource = tileB.SourceId;

        await ClearTileAsync(a, ct).ConfigureAwait(false);
        await ClearTileAsync(b, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(bSource))
            await BindTileAsync(a, bSource, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(aSource))
            await BindTileAsync(b, aSource, ct).ConfigureAwait(false);
    }

    public async Task ClearTileAsync(int index, CancellationToken ct = default)
    {
        await GetTile(index).StopAsync().ConfigureAwait(false);
        RaiseSession();
    }

    public async Task<AddSourceResult> AddSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default)
    {
        var result = Catalog.Add(source);
        if (result.Added)
        {
            await Catalog.SaveAsync(ct).ConfigureAwait(false);
            CatalogChanged?.Invoke();
        }

        return result;
    }

    public async Task<AddSourceResult> UpdateSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default)
    {
        var result = Catalog.Update(source);
        if (result.Added)
        {
            await Catalog.SaveAsync(ct).ConfigureAwait(false);
            CatalogChanged?.Invoke();
        }

        return result;
    }

    public async Task<bool> RemoveSourceAsync(string sourceId, CancellationToken ct = default)
    {
        var source = Catalog.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source is null) return false;

        if (!Catalog.Remove(sourceId)) return false;

        if (!string.IsNullOrWhiteSpace(source.CoverKey))
            Covers.Delete(source.CoverKey);

        Covers.RemoveOrphans(Catalog.Sources.Select(s => s.CoverKey));

        await Catalog.SaveAsync(ct).ConfigureAwait(false);
        CatalogChanged?.Invoke();
        return true;
    }

    public async Task<MediaProbeResult> TestSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default)
    {
        var light = await Probe.ProbeAsync(source.Endpoint, ct).ConfigureAwait(false);
        if (!light.NeedsBackend || !Backend.IsAvailable)
            return light;

        return await Backend.ProbeAsync(source.Endpoint, ct).ConfigureAwait(false);
    }

    public async Task<CoverCaptureOutcome> RefreshCoverAsync(VideoSourceDescriptor source, CancellationToken ct = default)
    {
        var outcome = await CoverCapture.CaptureAsync(source.Endpoint, ct).ConfigureAwait(false);
        if (outcome.Ok && outcome.CoverKey is not null)
        {
            Catalog.UpdateValidation(
                source.Id,
                SourceValidationState.Ok,
                outcome.CoverKey,
                outcome.CapturedAt,
                lastPlayedAt: source.LastPlayedAt);

            await Catalog.SaveAsync(ct).ConfigureAwait(false);
            CatalogChanged?.Invoke();
        }

        return outcome;
    }

    public void Dispose() => Stop();

    private TileSession GetTile(int index)
    {
        lock (_gate)
        {
            var tile = _tiles.FirstOrDefault(t => t.Index == index);
            if (tile is null)
                throw new ArgumentOutOfRangeException(nameof(index), $"格索引超出布局：{index}");
            return tile;
        }
    }

    private void EnsureTiles(WallLayout layout)
    {
        _tiles = Enumerable.Range(0, layout.TileCount)
            .Select(i => new TileSession(i, Backend))
            .ToList();

        foreach (var tile in _tiles)
            tile.Changed += _ => RaiseSession();
    }

    private async Task SaveCatalogAsync()
    {
        await Catalog.SaveAsync().ConfigureAwait(false);
        CatalogChanged?.Invoke();
    }

    private void RaiseSession() => SessionChanged?.Invoke();
}