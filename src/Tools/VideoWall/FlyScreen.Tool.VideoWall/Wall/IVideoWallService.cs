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

    /// <summary>应用内「最大化」的格索引；null 表示正常墙布局。</summary>
    int? FocusedTileIndex { get; }

    /// <summary>
    /// 聚焦某格（暂停其余播放中的格，UI 将该格铺满）；传 null 恢复并继续此前被暂停的格。
    /// </summary>
    Task FocusTileAsync(int? tileIndex, CancellationToken ct = default);

    void Start();
    void Stop();

    Task InitializeAsync(CancellationToken ct = default);

    Task SetLayoutAsync(WallLayout layout, CancellationToken ct = default);
    Task BindTileAsync(int index, string? sourceId, CancellationToken ct = default);
    Task BindTileAsync(int index, string? sourceId, bool autoStart, CancellationToken ct = default);
    Task StartTileAsync(int index, CancellationToken ct = default);
    Task SwapTilesAsync(int a, int b, CancellationToken ct = default);
    Task ClearTileAsync(int index, CancellationToken ct = default);
    /// <summary>停止并回收全部画布格播放（切到列表/卡片或离开墙时调用）。</summary>
    Task StopAllTilesAsync(CancellationToken ct = default);

    /// <summary>画布是否开启自动轮播（关：只显示第 1 页）。</summary>
    bool CarouselEnabled { get; }

    int CarouselPage { get; }
    int CarouselPageCount { get; }
    int CarouselIntervalMs { get; }

    /// <summary>设置参与画布/轮播的源顺序（通常为当前分组+搜索过滤结果）。</summary>
    void SetCarouselSources(IReadOnlyList<string> sourceIds);

    Task SetCarouselEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>翻到下一页并按布局重绑格（仅 Enabled 时有意义；UI 定时器调用）。</summary>
    Task AdvanceCarouselAsync(CancellationToken ct = default);

    /// <summary>翻到上一页并按布局重绑格（仅 Enabled 时有意义）。</summary>
    Task RetreatCarouselAsync(CancellationToken ct = default);

    /// <summary>跳到指定页（0-based）并按布局重绑格（仅 Enabled 时有意义）。</summary>
    Task GoToCarouselPageAsync(int page, CancellationToken ct = default);

    /// <summary>
    /// 按当前页把源绑到墙格。<paramref name="autoStart"/> 为 false 时由宿主绑帧面后再 StartTile。
    /// </summary>
    Task ApplyCarouselPageAsync(bool autoStart = false, CancellationToken ct = default);

    Task<AddSourceResult> AddSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default);

    /// <summary>批量入库：一次落盘、一次 <see cref="CatalogChanged"/>，避免枚举竞态与 thrashing。</summary>
    Task<IReadOnlyList<AddSourceResult>> AddSourcesAsync(
        IEnumerable<VideoSourceDescriptor> sources,
        CancellationToken ct = default);

    Task<AddSourceResult> UpdateSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default);
    Task<bool> RemoveSourceAsync(string sourceId, CancellationToken ct = default);
    Task<MediaProbeResult> TestSourceAsync(VideoSourceDescriptor source, CancellationToken ct = default);
    Task<CoverCaptureOutcome> RefreshCoverAsync(VideoSourceDescriptor source, CancellationToken ct = default);
}