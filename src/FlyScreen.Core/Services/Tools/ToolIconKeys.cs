namespace FlyScreen.Services.Tools;

/// <summary>清单 icon 字段约定键；位图由工具程序集经 <see cref="ToolIconRegistry"/> 贡献。</summary>
public static class ToolIconKeys
{
    public const string ColorPicker = "tool-color-picker";
    public const string SharedDevices = "tool-shared-devices";

    /// <summary>是否已有工具贡献的位图（优先于宿主内嵌回退）。</summary>
    public static bool HasRegisteredBitmap(string? iconKey)
        => ToolIconRegistry.TryReadBitmapBytes(iconKey) != null;
}
