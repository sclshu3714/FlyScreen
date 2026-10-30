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
