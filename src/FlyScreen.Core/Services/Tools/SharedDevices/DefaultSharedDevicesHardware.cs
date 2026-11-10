namespace FlyScreen.Services.Tools.SharedDevices;

/// <summary>无法细枚举时提供「系统默认」占位，保证管理/接收 UI 可选。</summary>
public sealed class DefaultSharedDevicesHardware : ISharedDevicesHardware
{
    public static DefaultSharedDevicesHardware Instance { get; } = new();

    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind)
    {
        if (!SharedDeviceKinds.IsValid(kind)) return [];
        var name = kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase)
            ? "系统默认摄像头"
            : kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase)
                ? "系统默认麦克风"
                : "系统默认扬声器";
        return
        [
            new SharedLocalDevice
            {
                Kind = kind.Trim().ToLowerInvariant(),
                Id = "default",
                Name = name,
                IsDefault = true
            }
        ];
    }
}
