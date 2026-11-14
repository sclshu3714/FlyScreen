using FlyScreen.Services;
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

    public VideoWallService()
    {
        // Module 字段初始化时宿主数据目录尚不可用；用临时目录占位，InitializeAsync 再切到正式路径。
        // MUST NOT 传空字符串（Catalog/CoverCache 等会 ArgumentException）。
        var staging = Path.Combine(Path.GetTempPath(), "FlyScreen", "video-wall-uninit");
        Directory.CreateDirectory(Path.Combine(staging, "covers"));

        Catalog = new VideoSourceCatalog(Path.Combine(staging, SourcesFile));
        Covers = new CoverCache(Path.Combine(staging, "covers"));
        Credentials = new CredentialStore(Path.Combine(staging, CredentialsFile));
        Scenes = new SceneStore(Path.Combine(staging, ScenesFile));
        Recent = new RecentSourcesStore(Path.Combine(staging, RecentFile));
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

        // ALC LoadFromStream 后 Location 为空，须先绑定安装目录再初始化 LibVLC
        VlcNativeBootstrap.SetNativeRoot(ResolveNativeInstallDir());
        var vlc = new VlcMediaBackend(Credentials);
        Backend = vlc.IsAvailable ? vlc : new NullMediaBackend();
        if (!vlc.IsAvailable)
            AppLog.Warn("视频墙：LibVLC 原生库未就绪（检查 Tools/_native/libvlc 或 Installed/video-wall/*/libvlc）");
        else if (!string.IsNullOrEmpty(VlcNativeBootstrap.LastResolvedDirectory))
            AppLog.Info($"视频墙：LibVLC ← {VlcNativeBootstrap.LastResolvedDirectory}");

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

        // 先卸旧格播放器（await），再换新布局；禁止 fire-and-forget Dispose 导致残留画面/句柄
        List<TileSession> oldTiles;
        lock (_gate)
        {
            oldTiles = _tiles.ToList();
            _tiles = [];
            _layout = layout;
        }

        foreach (var tile in oldTiles)
        {
            try { await tile.StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        lock (_gate)
            EnsureTiles(layout);

        RaiseSession();
    }

    public async Task StopAllTilesAsync(CancellationToken ct = default)
    {
        List<TileSession> tiles;
        lock (_gate)
            tiles = _tiles.ToList();

        foreach (var tile in tiles)
        {
            ct.ThrowIfCancellationRequested();
            try { await tile.StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        RaiseSession();
    }

    public Task BindTileAsync(int index, string? sourceId, CancellationToken ct = default)
        => BindTileAsync(index, sourceId, autoStart: true, ct);

    /// <param name="autoStart">
    /// false 时只创建播放器，等宿主把原生引擎绑到格内 VideoView 后再 <see cref="StartTileAsync"/>，
    /// 避免无 HWND 时 LibVLC 弹出独立窗口。
    /// </param>
    public async Task BindTileAsync(int index, string? sourceId, bool autoStart, CancellationToken ct = default)
    {
        var tile = GetTile(index);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            await tile.StopAsync().ConfigureAwait(false);
            RaiseSession();
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
            ct,
            autoStart).ConfigureAwait(false);

        RaiseSession();
    }

    public Task StartTileAsync(int index, CancellationToken ct = default)
        => GetTile(index).StartBoundPlaybackAsync(ct);

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

    private static string? ResolveNativeInstallDir()
    {
        if (AppContext.GetData("FlyScreen.Tool.InstallDir.video-wall") is string fromHost
            && !string.IsNullOrWhiteSpace(fromHost)
            && Directory.Exists(fromHost))
            return fromHost;

        try
        {
            var root = Path.Combine(FlyScreenDataPaths.ToolsInstalledDirectory, VideoWallIds.ToolId);
            if (!Directory.Exists(root)) return null;
            foreach (var ver in Directory.EnumerateDirectories(root)
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(ver, "libvlc", "win-x64", "libvlc.dll"))
                    || File.Exists(Path.Combine(ver, "runtimes", "win-x64", "native", "libvlc.dll")))
                    return ver;
            }
        }
        catch { /* ignore */ }

        return null;
    }
}