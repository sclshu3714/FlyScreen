namespace FlyScreen.ToolSdk;

public static class SharedDeviceKinds
{
    public const string Speaker = "speaker";
    public const string Microphone = "microphone";
    public const string Camera = "camera";
    public static readonly string[] All = [Speaker, Microphone, Camera];

    public static bool IsValid(string? kind)
        => All.Any(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase));
}

/// <summary>本机可共享硬件枚举（宿主平台实现，工具只认此契约）。</summary>
public interface ISharedDevicesHardware
{
    IReadOnlyList<SharedLocalDevice> Enumerate(string kind);
}

public sealed class SharedLocalDevice
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
}

public sealed class NullSharedDevicesHardware : ISharedDevicesHardware
{
    public static NullSharedDevicesHardware Instance { get; } = new();
    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind) => [];
}

/// <summary>
/// 无法细枚举的平台占位：返回空，避免无硬件机被宣告为可提供。
/// Mac/Linux 等接入真实枚举前，对端会显示「(无设备)」。
/// </summary>
public sealed class DefaultSharedDevicesHardware : ISharedDevicesHardware
{
    public static DefaultSharedDevicesHardware Instance { get; } = new();

    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind) => [];
}

/// <summary>
/// 共享设备运行时（由工具模块注册）。宿主只通过本接口挂硬件/音频，不引用工具具体类型。
/// </summary>
public interface ISharedDevicesRuntime
{
    event Action<string>? StatusMessage;
    event Action? SessionChanged;
    event Action<string, bool>? ProviderTestCommand;

    bool IsProviderSessionActive { get; }

    void AttachHardware(ISharedDevicesHardware hardware);

    /// <summary>挂接投影音频引擎（类型为宿主 <c>IProjectionAudioEngine</c>）。</summary>
    void AttachAudioEngine(object audioEngine);

    void PublishStatus(string message);
    string? GetSelectedDeviceId(string kind);
    void PostCameraJpeg(byte[] jpeg);
    void PostMicAudio(ReadOnlySpan<byte> pcm, int sampleRate, int channels);
}
