using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy;

/// <summary>端口代理卡片图标（嵌入本程序集）。</summary>
public sealed class PortProxyIcons : IToolIconContribution
{
    public static PortProxyIcons Instance { get; } = new();

    public string IconKey => "tool-port-proxy";

    public Stream? OpenBitmap()
    {
        const string name = "FlyScreen.Tool.PortProxy.Icons.tool-port-proxy.png";
        return typeof(PortProxyIcons).Assembly.GetManifestResourceStream(name);
    }

    public static void EnsureRegistered()
        => FlyScreen.Services.Tools.ToolIconRegistry.Register(Instance);
}
