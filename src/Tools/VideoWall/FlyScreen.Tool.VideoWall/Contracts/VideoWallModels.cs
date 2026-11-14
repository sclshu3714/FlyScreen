namespace FlyScreen.Tool.VideoWall.Contracts;

/// <summary>
/// 视频源端点。地址中 MUST NOT 含明文凭据：账号密码经 <see cref="CredentialRef"/> 指向系统凭据表。
/// </summary>
public sealed record VideoEndpoint
{
    /// <summary>scheme：file / http / https / hls / dash / rtsp / rtmp / rtmps / srt / udp / rtp。</summary>
    public required string Protocol { get; init; }

    /// <summary>归一化后的地址（凭据已剔除，签名参数按规则处理）。</summary>
    public required string Address { get; init; }

    /// <summary>RTSP 传输方式：tcp / udp；空表示自动（一期默认 tcp）。</summary>
    public string? Transport { get; init; }

    /// <summary>抖动缓冲（毫秒）；null 表示按后端默认。</summary>
    public int? BufferMs { get; init; }

    /// <summary>自定义 HTTP Header（少量场景需要 Referer / User-Agent / Cookie）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>凭据引用键（对应凭据表条目）；null 表示匿名访问。</summary>
    public string? CredentialRef { get; init; }
}

/// <summary>源分组（工具页左栏/筛选用）。</summary>
public sealed record VideoSourceGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

/// <summary>固定/临时源条目。</summary>
public sealed record VideoSourceDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required VideoSourceKind Kind { get; init; }
    public required VideoSourceOrigin Origin { get; init; }
    public required VideoEndpoint Endpoint { get; init; }

    /// <summary>分组 Id（固定源）；临时源为 null。</summary>
    public string? GroupId { get; init; }

    /// <summary>解码偏好：auto / hardware / software。</summary>
    public string DecodePreference { get; init; } = "auto";

    /// <summary>备注。</summary>
    public string? Note { get; init; }

    /// <summary>最近一次抓帧/播放的判定结果（UI 状态点依据）。</summary>
    public SourceValidationState ValidationState { get; init; } = SourceValidationState.Unknown;

    /// <summary>连续失败次数；成功即归零。</summary>
    public int FailureCount { get; init; }

    /// <summary>封面缓存键（对应 <c>covers/&lt;key&gt;.jpg</c>）；无封面为 null。</summary>
    public string? CoverKey { get; init; }

    public DateTimeOffset? CoverCapturedAt { get; init; }

    public DateTimeOffset? LastPlayedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>墙布局。仅允许 1×1 / 2×2 / 3×3 / 4×4；比例布局（主画面+侧栏）属 P1。</summary>
public sealed record WallLayout
{
    public required int Rows { get; init; }
    public required int Columns { get; init; }

    public int TileCount => Rows * Columns;

    public static readonly WallLayout Single = new() { Rows = 1, Columns = 1 };
    public static readonly WallLayout TwoByTwo = new() { Rows = 2, Columns = 2 };
    public static readonly WallLayout ThreeByThree = new() { Rows = 3, Columns = 3 };
    public static readonly WallLayout FourByFour = new() { Rows = 4, Columns = 4 };

    /// <summary>按边长取预设；非 1/2/3/4 返回 null（调用方 MUST 拒绝并提示）。</summary>
    public static WallLayout? FromEdge(int edge) => edge switch
    {
        1 => Single,
        2 => TwoByTwo,
        3 => ThreeByThree,
        4 => FourByFour,
        _ => null
    };
}

/// <summary>单格绑定：为空表示空格。</summary>
public sealed record TileBinding
{
    public required int Index { get; init; }

    /// <summary>固定源 Id；临时源时为 null。</summary>
    public string? SourceId { get; init; }

    /// <summary>临时源地址（固定源时为 null）。</summary>
    public string? TemporaryAddress { get; init; }

    /// <summary>画面适配：uniform / cover / fill。</summary>
    public string Fit { get; init; } = "uniform";

    public bool Muted { get; init; } = true;
}

/// <summary>电视墙场景快照（布局 + 每格绑定 + 音频策略 + 合流设置）。</summary>
public sealed record WallScene
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required WallLayout Layout { get; init; }
    public IReadOnlyList<TileBinding> Tiles { get; init; } = [];
    public AudioPolicy Audio { get; init; } = AudioPolicy.Mute;

    /// <summary>独奏格索引（<see cref="AudioPolicy.Solo"/> 时有效）。</summary>
    public int? SoloTile { get; init; }

    /// <summary>合流视图是否绘制名称标注。</summary>
    public bool CompositeShowLabels { get; init; }

    /// <summary>工作区打开时自动加载本场景。</summary>
    public bool AutoStart { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>单格运行统计（用于信息条与诊断）。</summary>
public sealed record MediaStats
{
    public int BitrateKbps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }

    /// <summary>抖动缓冲实际深度（毫秒）。</summary>
    public int BufferMs { get; init; }

    /// <summary>首帧耗时（毫秒）；-1 表示尚未出帧。</summary>
    public int FirstFrameMs { get; init; } = -1;

    public int DroppedFrames { get; init; }

    /// <summary>是否硬解（false 表示软解或未知）。</summary>
    public bool HardwareDecode { get; init; }
}

/// <summary>探测结果（对应 UI「测试」按钮）。</summary>
public sealed record MediaProbeResult
{
    public bool Ok { get; init; }
    public VideoErrorCode Error { get; init; } = VideoErrorCode.None;

    /// <summary>面向用户的失败原因（已本地化）。</summary>
    public string? Message { get; init; }

    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }

    /// <summary>轻量探测无法完成协议握手时为 true（需完整后端探测）。</summary>
    public bool NeedsBackend { get; init; }
}

/// <summary>首帧封面抓取结果（<see cref="IMediaBackend.CaptureCoverAsync"/>）。</summary>
public sealed record CoverCaptureResult
{
    public bool Ok { get; init; }
    public byte[]? JpegBytes { get; init; }
    public VideoErrorCode Error { get; init; } = VideoErrorCode.None;
    public string? Message { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? VideoCodec { get; init; }
    public double Fps { get; init; }
}
