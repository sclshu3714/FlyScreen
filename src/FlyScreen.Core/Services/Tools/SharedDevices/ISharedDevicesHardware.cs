namespace FlyScreen.Services.Tools.SharedDevices;

/// <summary>本机可共享硬件枚举与当前选用（提供方侧）。</summary>
public interface ISharedDevicesHardware
{
    IReadOnlyList<SharedLocalDevice> Enumerate(string kind);
}

/// <summary>本机一路物理设备。</summary>
public sealed class SharedLocalDevice
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>无硬件枚举能力时的空实现。</summary>
public sealed class NullSharedDevicesHardware : ISharedDevicesHardware
{
    public static NullSharedDevicesHardware Instance { get; } = new();

    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind) => [];
}
