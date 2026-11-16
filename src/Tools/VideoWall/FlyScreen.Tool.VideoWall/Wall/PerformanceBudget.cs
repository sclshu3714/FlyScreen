using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary>简单解码预算估算（用于 9×4K 等超载提示）。</summary>
public static class PerformanceBudget
{
    private const int FourKWeight = 120;
    private const int BudgetLimit = 400;

    public static int EstimateWeight(int width, int height, double fps, string? codec)
    {
        var pixels = (long)width * height;
        var baseWeight = pixels switch
        {
            >= 3840 * 2160 => FourKWeight,
            >= 1920 * 1080 => 40,
            >= 1280 * 720 => 20,
            _ => 10
        };

        var fpsFactor = Math.Clamp(fps / 25.0, 0.5, 2.0);
        var codecFactor = codec?.ToLowerInvariant() switch
        {
            "hevc" or "h265" or "av1" => 1.4,
            "mpeg2video" => 0.8,
            _ => 1.0
        };

        return (int)Math.Ceiling(baseWeight * fpsFactor * codecFactor);
    }

    public static int TotalWeight(IEnumerable<MediaStats> activeStats)
        => activeStats.Sum(s => EstimateWeight(s.Width, s.Height, s.Fps, null));

    public static bool IsOverBudget(WallLayout layout, IEnumerable<MediaStats> activeStats)
    {
        var total = TotalWeight(activeStats);
        var tileFactor = layout.TileCount;
        return total > BudgetLimit || (layout.Rows >= 3 && activeStats.Count(s => s.Width >= 3840) >= 9);
    }
}