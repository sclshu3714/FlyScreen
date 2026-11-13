using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>兼容旧名；实现已迁至 <see cref="FlyScreen.ToolSdk.DefaultSharedDevicesHardware"/>。</summary>
public sealed class DefaultSharedDevicesHardware : ISharedDevicesHardware
{
    public static DefaultSharedDevicesHardware Instance { get; } = new();

    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind)
        => ToolSdk.DefaultSharedDevicesHardware.Instance.Enumerate(kind);
}
