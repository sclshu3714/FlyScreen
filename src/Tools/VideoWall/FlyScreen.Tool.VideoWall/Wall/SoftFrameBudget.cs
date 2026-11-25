namespace FlyScreen.Tool.VideoWall.Wall;

/// <summary>软件帧 / 宫格解码预算：控制出画分辨率与帧率，避免多路拖死 CPU/GPU。</summary>
public static class SoftFrameBudget
{
    /// <summary>单路播放默认最大宽。</summary>
    public const int SingleMaxWidth = 1280;

    /// <summary>按宫格数量给软件帧最大宽。</summary>
    public static int MaxWidthForWall(int tileCount)
        => tileCount switch
        {
            <= 1 => SingleMaxWidth,
            <= 4 => 720,
            _ => 480
        };

    /// <summary>宫格软件帧建议上限 fps（0=不限）。</summary>
    public static int MaxFpsForWall(int tileCount)
        => tileCount switch
        {
            <= 1 => 0,
            <= 4 => 20,
            _ => 12
        };

    /// <summary>宫格网络缓存（毫秒）。</summary>
    public static int NetworkCacheMsForWall => 800;
}
