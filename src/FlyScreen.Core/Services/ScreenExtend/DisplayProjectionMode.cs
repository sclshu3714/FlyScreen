namespace FlyScreen.Services.ScreenExtend;

/// <summary>对齐系统 Win+P；UI 仅暴露复制/扩展，Internal 仅用于停止时切回本机。</summary>
public enum DisplayProjectionMode
{
    /// <summary>仅电脑屏幕（停止投影时系统侧使用）</summary>
    Internal = 0,
    /// <summary>复制</summary>
    Clone = 1,
    /// <summary>扩展</summary>
    Extend = 2,
    /// <summary>仅第二屏幕（已从 UI 移除，旧设置会钳到 Extend）</summary>
    External = 3
}

public static class DisplayProjectionModeInfo
{
    public static string Label(DisplayProjectionMode mode) => mode switch
    {
        DisplayProjectionMode.Internal => "仅电脑屏幕",
        DisplayProjectionMode.Clone => "复制",
        DisplayProjectionMode.Extend => "扩展",
        DisplayProjectionMode.External => "扩展",
        _ => "扩展"
    };

    public static string Description(DisplayProjectionMode mode) => mode switch
    {
        DisplayProjectionMode.Clone => "将本机主屏画面镜像到目标设备",
        DisplayProjectionMode.Extend => "扩展桌面到目标（需真副屏或虚拟显示驱动）",
        DisplayProjectionMode.Internal => "停止投影",
        _ => "扩展桌面到目标（需真副屏或虚拟显示驱动）"
    };

    /// <summary>设置/旧值钳制：仅保留复制与扩展供业务选择。</summary>
    public static DisplayProjectionMode ClampForPicker(int mode) => mode switch
    {
        1 => DisplayProjectionMode.Clone,
        _ => DisplayProjectionMode.Extend
    };

    public static DisplayProjectionMode Clamp(int mode)
        => mode is >= 0 and <= 3 ? (DisplayProjectionMode)mode : DisplayProjectionMode.Extend;

    /// <summary>弹窗下拉可选方式。</summary>
    public static IReadOnlyList<DisplayProjectionMode> PickerModes { get; } =
    [
        DisplayProjectionMode.Clone,
        DisplayProjectionMode.Extend
    ];

    [Obsolete("改用 PickerModes")]
    public static IReadOnlyList<DisplayProjectionMode> All => PickerModes;
}
