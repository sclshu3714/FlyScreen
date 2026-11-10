using System.IO;
using Microsoft.Win32;

namespace FlyScreen.Uninstaller.Services;

public sealed class UninstallContext
{
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FlyScreen";
    public const string UninstallExeName = "Uninstall.exe";

    public string ProductName { get; init; } = "玄鸟";
    public string Version { get; init; } = "";
    public string InstallDir { get; init; } = "";
    public string ExeName { get; init; } = "FlyScreen.exe";
    public bool Found { get; init; }

    public static UninstallContext Detect(string[] args)
    {
        string? installDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--install-dir" or "/install-dir" && i + 1 < args.Length)
                installDir = args[++i].Trim('"');
        }

        string product = "玄鸟";
        string version = "";
        string exeName = "FlyScreen.exe";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (key != null)
            {
                product = key.GetValue("DisplayName") as string ?? product;
                version = key.GetValue("DisplayVersion") as string ?? version;
                installDir ??= key.GetValue("InstallLocation") as string;
                var icon = key.GetValue("DisplayIcon") as string;
                if (!string.IsNullOrWhiteSpace(icon) && File.Exists(icon))
                    exeName = Path.GetFileName(icon);
            }
        }
        catch { /* ignore */ }

        // 默认：卸载程序自身所在目录
        if (string.IsNullOrWhiteSpace(installDir))
            installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        installDir = Path.GetFullPath(installDir.Trim().TrimEnd('\\', '/'));
        var found = Directory.Exists(installDir);

        return new UninstallContext
        {
            ProductName = product,
            Version = version,
            InstallDir = installDir,
            ExeName = exeName,
            Found = found
        };
    }
}
