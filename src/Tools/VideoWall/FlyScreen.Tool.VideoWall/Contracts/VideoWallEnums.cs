namespace FlyScreen.Tool.VideoWall.Contracts;

/// <summary>源类型（决定 UI 图标与地址编辑形态）。</summary>
public enum VideoSourceKind
{
    /// <summary>本机或 IP 摄像头（RTSP / 本机 UVC）。</summary>
    Camera,

    /// <summary>NVR 通道（由模板批量生成）。</summary>
    NvrChannel,

    /// <summary>网络地址（HLS / DASH / RTMP / SRT / HTTP 文件）。</summary>
    NetworkUrl,

    /// <summary>本机文件。</summary>
    LocalFile,

    /// <summary>ONVIF 发现设备（P1）。</summary>
    OnvifDevice,

    /// <summary>GB28181 通道（P1）。</summary>
    Gb28181Channel
}

/// <summary>源来源：固定目录 vs 一次性临时源。</summary>
public enum VideoSourceOrigin
{
    Fixed,
    Temporary
}

/// <summary>单格状态机（与文档 §5.4 一致）。</summary>
public enum TilePlayState
{
    Idle,
    Resolving,
    Connecting,
    Buffering,
    Playing,
    Paused,
    Stalled,
    Reconnecting,
    Failed,
    Stopping,
    Stopped
}

/// <summary>错误分类（与文档 §8 一致，用于格内提示与日志）。</summary>
public enum VideoErrorCode
{
    None,
    NetDns,
    NetTimeout,
    NetReset,
    AuthRequired,
    AuthForbidden,
    SourceNotFound,
    SourceUnsupported,
    DecodeHardwareFallback,
    DecodeFailed,
    PerformanceOverBudget,
    IoDisk
}

/// <summary>源的可用性判定结果（由首帧抓取或最近一次播放写入）。</summary>
public enum SourceValidationState
{
    /// <summary>从未验证（新建但未抓帧 / 未播放）。</summary>
    Unknown,

    /// <summary>最近一次抓帧或播放成功。</summary>
    Ok,

    /// <summary>最近一次抓帧或播放失败。</summary>
    Failed
}

/// <summary>合流/整墙音频策略。</summary>
public enum AudioPolicy
{
    /// <summary>全部静音（默认）。</summary>
    Mute,

    /// <summary>只放指定格（独奏）。</summary>
    Solo,

    /// <summary>全部混音（P2）。</summary>
    Mix
}
