using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault;

public sealed class FileVaultIcons : IToolIconContribution
{
    public static FileVaultIcons Instance { get; } = new();

    public string IconKey => "tool-file-vault";

    public Stream? OpenBitmap()
    {
        const string name = "FlyScreen.Tool.FileVault.Icons.tool-file-vault.png";
        return typeof(FileVaultIcons).Assembly.GetManifestResourceStream(name);
    }

    public static void EnsureRegistered()
        => FlyScreen.Services.Tools.ToolIconRegistry.Register(Instance);
}
