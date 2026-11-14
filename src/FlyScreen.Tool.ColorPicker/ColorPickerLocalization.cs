using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.ColorPicker;

/// <summary>颜色拾取器文案（独立于主程序 Loc 表）。</summary>
public sealed class ColorPickerLocalization : IToolLocalization
{
    public static ColorPickerLocalization Instance { get; } = new();

    static ColorPickerLocalization() => EnsureRegistered();

    public string SourceId => "color-picker";

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } = Build();

    public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build()
        => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = Zh(),
            ["en-US"] = En()
        };

    private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.ColorPicker.Name"] = "颜色拾取器",
        ["Tool.ColorPicker.Summary"] = "用滴管拾取屏幕任意位置颜色，并转换为 HEX / RGB / HSL / CMYK 等常用格式。",
        ["Tool.ColorPicker.Pick"] = "颜色拾取器",
        ["Tool.ColorPicker.Picking"] = "拾取中… 再点取消",
        ["Tool.ColorPicker.Preview"] = "颜色预览",
        ["Tool.ColorPicker.Formats"] = "颜色格式",
        ["Tool.ColorPicker.Copied"] = "已复制",
        ["Tool.ColorPicker.Hint"] = "点击「颜色拾取器」后鼠标变为滴管；移动时实时预览，单击锁定颜色；再点按钮可取消拾取。",
        ["Tool.ColorPicker.Unsupported"] = "当前平台不支持屏幕取色。"
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.ColorPicker.Name"] = "Color picker",
        ["Tool.ColorPicker.Summary"] = "Eyedrop any on-screen pixel and convert it to HEX / RGB / HSL / CMYK and other common formats.",
        ["Tool.ColorPicker.Pick"] = "Color picker",
        ["Tool.ColorPicker.Picking"] = "Picking… click again to cancel",
        ["Tool.ColorPicker.Preview"] = "Preview",
        ["Tool.ColorPicker.Formats"] = "Color formats",
        ["Tool.ColorPicker.Copied"] = "Copied",
        ["Tool.ColorPicker.Hint"] = "Click Color picker for an eyedropper cursor; move to preview live, click to lock; click the button again to cancel.",
        ["Tool.ColorPicker.Unsupported"] = "Screen sampling is not supported on this platform."
    };
}
