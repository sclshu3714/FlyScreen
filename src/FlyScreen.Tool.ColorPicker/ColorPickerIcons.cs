using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.ColorPicker;

/// <summary>颜色拾取器卡片/快捷栏图标（嵌入本程序集）。</summary>
public sealed class ColorPickerIcons : IToolIconContribution
{
    public static ColorPickerIcons Instance { get; } = new();

    public string IconKey => "tool-color-picker";

    public Stream? OpenBitmap()
    {
        const string name = "FlyScreen.Tool.ColorPicker.Icons.tool-color-picker.jpg";
        return typeof(ColorPickerIcons).Assembly.GetManifestResourceStream(name);
    }

    public static void EnsureRegistered()
        => FlyScreen.Services.Tools.ToolIconRegistry.Register(Instance);
}
