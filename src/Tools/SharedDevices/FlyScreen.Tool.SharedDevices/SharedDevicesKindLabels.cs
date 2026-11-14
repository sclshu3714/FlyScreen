using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>设备种类显示名（原 Core ToolCardRows.LocalizeKind）。</summary>
public static class SharedDevicesKindLabels
{
    public static string Localize(string kind) => kind.ToLowerInvariant() switch
    {
        "speaker" => Loc.T("Tool.SharedDevices.Speaker"),
        "microphone" => Loc.T("Tool.SharedDevices.Microphone"),
        "camera" => Loc.T("Tool.SharedDevices.Camera"),
        _ => kind
    };
}
