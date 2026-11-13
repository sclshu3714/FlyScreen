namespace FlyScreen.Tool.VideoWall.Contracts;

/// <summary>
/// 播放内核抽象。一期实现 <c>Vlc</c>（LibVLCSharp），二期实现 <c>Ffmpeg</c>；
/// 上层（墙、场景、UI）MUST NOT 直接引用任何内核类型。
/// </summary>
public interface IMediaBackend
{
    /// <summary>内核标识：<c>vlc</c> / <c>ffmpeg</c>。</summary>
    string Name { get; }

    /// <summary>本机可用性（原生库是否就位、是否被许可裁剪）。</summary>
    bool IsAvailable { get; }

    /// <summary>协议探测（不建播放会话）。</summary>
    Task<MediaProbeResult> ProbeAsync(VideoEndpoint endpoint, CancellationToken ct = default);

    /// <summary>创建单路播放器（一个 MediaPlayer 对应一格）。</summary>
    IMediaPlayer CreatePlayer(VideoEndpoint endpoint, MediaPlayerOptions options);
}

/// <summary>单路播放器选项。</summary>
public sealed record MediaPlayerOptions
{
    public string DecodePreference { get; init; } = "auto";
    public int? BufferMs { get; init; }
    public bool Mute { get; init; } = true;

    /// <summary>渲染目标标识：平台 UI 卫星提供的原生面句柄（HWND / SurfaceView），由后端解释。</summary>
    public IntPtr? RenderTarget { get; init; }
}

/// <summary>单路播放器。所有状态回调 MUST 在非 UI 线程触发，由上层调度到 UI 线程。</summary>
public interface IMediaPlayer
{
    TilePlayState State { get; }

    VideoErrorCode ErrorCode { get; }

    event Action<TilePlayState>? StateChanged;

    event Action<MediaStats>? StatsChanged;

    /// <summary>更新渲染区域（布局切换/全屏时调用，不重建连接）。</summary>
    void SetRenderTarget(IntPtr target);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    void SetVolume(int volume0To100);

    void SetMuted(bool muted);
}

/// <summary>
/// 合流画布（仅 FFmpeg 后端实现）：把 N 路帧合成到一张画布。
/// 一期若走 libVLC，合流以「按布局的二次渲染视图」交付，本接口返回 null。
/// </summary>
public interface IMediaCompositor
{
    bool IsAvailable { get; }

    void Attach(int tileIndex, IMediaPlayer player);

    void Detach(int tileIndex);

    void SetLayout(WallLayout layout);

    /// <summary>可选：把合流画面编码为单流输出（文件/推流），属 P2。</summary>
    Task StartOutputAsync(string targetUri, CancellationToken ct = default);
}
