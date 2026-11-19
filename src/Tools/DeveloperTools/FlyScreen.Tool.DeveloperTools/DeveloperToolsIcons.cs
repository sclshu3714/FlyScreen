using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.DeveloperTools;

public sealed class DeveloperToolsIcons : IToolIconContribution
{
    public static DeveloperToolsIcons Instance { get; } = new();

    public string IconKey => "tool-developer-tools";

    public Stream? OpenBitmap()
    {
        const string name = "FlyScreen.Tool.DeveloperTools.Icons.tool-developer-tools.png";
        return typeof(DeveloperToolsIcons).Assembly.GetManifestResourceStream(name);
    }

    public static void EnsureRegistered()
        => FlyScreen.Services.Tools.ToolIconRegistry.Register(Instance);
}
