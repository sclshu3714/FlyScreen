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

    public string ResolutionLabel => $"{Width}×{Height}";
    public string ScaleLabel => $"{ScalePercent}%";

    public ProjectionDisplayChoice Clone() => new()
    {
        Source = Source,
        Width = Width,
        Height = Height,
        ScalePercent = ScalePercent,
        RefreshHz = RefreshHz
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

    public static double ScaleToFactor(int percent) => Math.Max(0.5, percent / 100.0);
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
