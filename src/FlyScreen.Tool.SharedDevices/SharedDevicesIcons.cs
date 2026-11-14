using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices;

/// <summary>共享设备卡片/快捷栏图标（嵌入本程序集）。</summary>
public sealed class SharedDevicesIcons : IToolIconContribution
{
    public static SharedDevicesIcons Instance { get; } = new();

    public string IconKey => "tool-shared-devices";

    public Stream? OpenBitmap()
    {
        const string name = "FlyScreen.Tool.SharedDevices.Icons.tool-shared-devices.png";
        return typeof(SharedDevicesIcons).Assembly.GetManifestResourceStream(name);
    }

    public static void EnsureRegistered()
        => FlyScreen.Services.Tools.ToolIconRegistry.Register(Instance);
}
