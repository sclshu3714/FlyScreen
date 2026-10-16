namespace FlyScreen.Services.Input;

/// <summary>穿边模式（本机→对端与对端→本机共用同一策略）。</summary>
public enum EdgeCrossMode
{
    /// <summary>无障碍：靠近边缘并继续外推即可穿边（默认）。</summary>
    BarrierFree = 0,

    /// <summary>双击：1 秒内同侧边界撞击 2 次才穿边。</summary>
    DoubleTap = 1,

    /// <summary>极速：从较远处高速撞边才穿边；缓慢贴边不穿。</summary>
    SpeedBurst = 2
}
