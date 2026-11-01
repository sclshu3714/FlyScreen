using FlyScreen.Models;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>投影弹窗中的分辨率来源。</summary>
public enum ProjectionDisplaySource
{
    /// <summary>本机主屏（复制默认）。</summary>
    HostPrimary = 0,
    /// <summary>目标电脑主屏（扩展默认；开始前查询 / Accept 后再精调）。</summary>
    TargetPeer = 1,
    /// <summary>用户指定宽高。</summary>
    Custom = 2
}

/// <summary>投影输出分辨率与缩放选择。</summary>
public sealed class ProjectionDisplayChoice
{
    public ProjectionDisplaySource Source { get; set; } = ProjectionDisplaySource.HostPrimary;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    /// <summary>100=100%。副屏 DPI 受限时仍保存供 UI / 协商。</summary>
    public int ScalePercent { get; set; } = 100;
    public int RefreshHz { get; set; } = 60;
    /// <summary>手机投影：裁掉画面四周黑边。默认关闭，按原画面发送。</summary>
    public bool CropBlackBars { get; set; }
    /// <summary>采集帧率。默认 20。</summary>
    public int Fps { get; set; } = ProjectionDisplayPresets.DefaultFps;
    /// <summary>JPEG 质量 1–100。默认 60。</summary>
    public int JpegQuality { get; set; } = ProjectionDisplayPresets.DefaultJpegQuality;

    public string ResolutionLabel => $"{Width}×{Height}";
    public string ScaleLabel => $"{ScalePercent}%";

    public ProjectionDisplayChoice Clone() => new()
    {
        Source = Source,
        Width = Width,
        Height = Height,
        ScalePercent = ScalePercent,
        RefreshHz = RefreshHz,
        CropBlackBars = CropBlackBars,
        Fps = Fps,
        JpegQuality = JpegQuality
    };
}

/// <summary>常用分辨率与缩放档位。</summary>
public static class ProjectionDisplayPresets
{
    public static IReadOnlyList<(int W, int H)> Resolutions { get; } =
    [
        (1280, 720),
        (1366, 768),
        (1600, 900),
        (1920, 1080),
        (1920, 1200),
        (2560, 1440),
        (2560, 1600),
        (3840, 2160)
    ];

    public static IReadOnlyList<int> ScalePercents { get; } =
        [100, 125, 150, 175, 200];

    public const int DefaultFps = 30;
    /// <summary>原画：0。不按画质档处理，采集到什么就送什么。</summary>
    public const int OriginalJpegQuality = 0;
    public const int DefaultJpegQuality = OriginalJpegQuality;

    public static IReadOnlyList<int> FrameRates { get; } =
        [12, 15, 20, 24, 30, 40, 50, 60, 75, 90, 120];

    /// <summary>从上到下：高画质、清晰、原画、标准、流畅。0 是原画。</summary>
    public static IReadOnlyList<int> JpegQualities { get; } = [100, 95, OriginalJpegQuality, 70, 45];

    public static string FpsLabel(int fps)
        => fps == DefaultFps ? $"{fps} 帧（推荐）" : $"{fps} 帧";

    public static string JpegQualityLabel(int quality) => ClampJpegQuality(quality) switch
    {
        100 => "高画质",
        95 => "清晰",
        OriginalJpegQuality => "原画",
        70 => "标准",
        45 => "流畅",
        _ => quality.ToString()
    };

    /// <summary>
    /// 原画不缩放、不降质，采集画面按 JPEG 100 原样送出。
    /// 高画质、清晰按所选分辨率编码（100 / 95），不再额外缩小。
    /// 标准、流畅降低 JPEG 质量，并把长边限制在 1600 / 960。
    /// </summary>
    public readonly record struct ProjectionPictureQuality(int EncodeQuality, int ExtraLongEdgeCap, bool Passthrough);

    public static ProjectionPictureQuality DescribeQuality(int quality)
    {
        var tier = ClampJpegQuality(quality);
        return tier switch
        {
            100 => new(100, 0, false),
            95 => new(95, 0, false),
            OriginalJpegQuality => new(100, 0, true),
            70 => new(70, 1600, false),
            _ => new(45, 960, false)
        };
    }

    public static int RememberedFps(AppSettings settings)
        => settings.ScreenExtendFps > 0 ? ClampFps(settings.ScreenExtendFps) : DefaultFps;

    public static int RememberedJpegQuality(AppSettings settings)
        => settings.ScreenExtendJpegQuality > 0
            ? ClampJpegQuality(settings.ScreenExtendJpegQuality)
            : DefaultJpegQuality;

    public static void WriteRemembered(AppSettings settings, ProjectionDisplayChoice choice, bool audioSync)
    {
        settings.ScreenExtendDisplaySource = (int)choice.Source;
        if (choice.Width > 0) settings.ScreenExtendWidth = choice.Width;
        if (choice.Height > 0) settings.ScreenExtendHeight = choice.Height;
        settings.ScreenExtendScalePercent = ClampScale(choice.ScalePercent > 0 ? choice.ScalePercent : 100);
        settings.ScreenExtendFps = ClampFps(choice.Fps > 0 ? choice.Fps : DefaultFps);
        settings.ScreenExtendJpegQuality = ClampJpegQuality(
            choice.JpegQuality > 0 ? choice.JpegQuality : DefaultJpegQuality);
        settings.ScreenExtendAudioSync = audioSync;
        settings.ScreenExtendCropBlackBars = choice.CropBlackBars;
    }

    public static int ClampScale(int percent)
    {
        if (percent < 50) return 100;
        if (percent > 500) return 200;
        // 对齐到最近档位
        var best = ScalePercents[0];
        var bestDiff = Math.Abs(percent - best);
        foreach (var s in ScalePercents)
        {
            var d = Math.Abs(percent - s);
            if (d < bestDiff)
            {
                best = s;
                bestDiff = d;
            }
        }

        return best;
    }

    public static int ClampFps(int fps) => Nearest(FrameRates, fps, DefaultFps);

    public static int ClampJpegQuality(int quality)
        => quality <= 0 ? OriginalJpegQuality : Nearest(JpegQualities, quality, 100);

    /// <summary>两帧之间的最短间隔。采集更慢时由编码自然拉长。</summary>
    public static int FpsToIntervalMs(int fps)
        => Math.Clamp(1000 / Math.Max(1, ClampFps(fps)), 8, 250);

    public static double ScaleToFactor(int percent) => Math.Max(0.5, percent / 100.0);

    private static int Nearest(IReadOnlyList<int> values, int value, int fallback)
    {
        if (values.Count == 0) return fallback;
        var best = values[0];
        var bestDiff = Math.Abs(value - best);
        foreach (var v in values)
        {
            var d = Math.Abs(value - v);
            if (d < bestDiff)
            {
                best = v;
                bestDiff = d;
            }
        }

        return best;
    }
}

/// <summary>手机投影采集缓冲尺寸。宽高比跟手机屏幕走，只把所选分辨率当作长边上限。</summary>
public readonly record struct ProjectionCaptureSize(int Width, int Height)
{
    /// <summary>
    /// 虚拟屏与手机比例不一致时，系统会把画面缩进黑边。接收端再等比放入显示器后，高和宽都会填不满。
    /// 这里保持手机当前朝向的比例，长边不超过 <paramref name="maxEdge"/>，也不超过所选分辨率的长边。
    /// </summary>
    public static ProjectionCaptureSize ForPhoneScreen(
        int screenW, int screenH, int choiceW, int choiceH, int maxEdge = 1920)
    {
        screenW = Math.Max(1, screenW);
        screenH = Math.Max(1, screenH);
        var screenLong = Math.Max(screenW, screenH);
        var screenShort = Math.Min(screenW, screenH);

        var capLong = screenLong;
        var choiceLong = Math.Max(0, choiceW);
        if (choiceH > choiceLong) choiceLong = choiceH;
        if (choiceLong > 0)
            capLong = Math.Min(capLong, choiceLong);
        if (maxEdge > 0)
            capLong = Math.Min(capLong, maxEdge);
        capLong = Math.Max(160, capLong);

        var capShort = (int)Math.Round(screenShort * (capLong / (double)screenLong));
        capShort = Math.Max(160, capShort);
        capLong = ToEven(capLong);
        capShort = ToEven(capShort);

        var landscape = screenW >= screenH;
        return landscape
            ? new ProjectionCaptureSize(capLong, capShort)
            : new ProjectionCaptureSize(capShort, capLong);
    }

    private static int ToEven(int value)
    {
        if (value < 160) return 160;
        return (value & 1) == 0 ? value : value - 1;
    }
}
