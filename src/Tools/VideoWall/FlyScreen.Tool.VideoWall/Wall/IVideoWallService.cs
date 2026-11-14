using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Player;
using FlyScreen.Tool.VideoWall.Sources;

namespace FlyScreen.Tool.VideoWall.Wall;

public interface IVideoWallService
{
    bool IsRunning { get; }

    event Action? SessionChanged;
    event Action? CatalogChanged;

    VideoSourceCatalog Catalog { get; }
    CoverCache Covers { get; }
    CoverCaptureService CoverCapture { get; }
    ProbeService Probe { get; }
    SceneStore Scenes { get; }
    RecentSourcesStore Recent { get; }
    CredentialStore Credentials { get; }

    IMediaBackend Backend { get; }
    WallLayout Layout { get; }
    IReadOnlyList<TileSession> Tiles { get; }
    SinglePlaybackController SinglePlayback { get; }
    AudioRouter Audio { get; }

    void Start();
    void Stop();

    Task InitializeAsync(CancellationToken ct = default);

    Task SetLayoutAsync(WallLayout layout, CancellationToken ct = default);
    Task BindTileAsync(int index, string? sourceId, CancellationToken ct = default);
    Task SwapTilesAsync(int a, int b, CancellationToken ct = default);
    Task ClearTileAsync(int index, CancellationToken ct = default);

    Task<AddSourceResult> AddSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default);
    Task<AddSourceResult> UpdateSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default);
    Task<bool> RemoveSourceAsync(string sourceId, CancellationToken ct = default);
    Task<MediaProbeResult> TestSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default);
    Task<CoverCaptureOutcome> RefreshCoverAsync(VideoSourceDescriptor source, CancellationToken ct = default);
}