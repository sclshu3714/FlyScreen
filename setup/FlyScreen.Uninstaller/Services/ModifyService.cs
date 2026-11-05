using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FlyScreen.Uninstaller.Services;

internal static class ModifyService
{
    public static void RepairShortcuts(string productName, string exePath, string workDir)
    {
        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            productName + ".lnk");
        CreateShortcut(desktop, exePath, workDir, productName);

        var linkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", productName);
        Directory.CreateDirectory(linkDir);
        CreateShortcut(Path.Combine(linkDir, productName + ".lnk"), exePath, workDir, productName);
    }

    public static void RepairUninstallRegistry(UninstallContext ctx, string? exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallContext.UninstallKeyPath);
        if (key == null) return;

        var uninstallExe = Path.Combine(ctx.InstallDir, UninstallContext.UninstallExeName);
        key.SetValue("DisplayName", ctx.ProductName);
        if (!string.IsNullOrWhiteSpace(ctx.Version))
            key.SetValue("DisplayVersion", ctx.Version);
        key.SetValue("Publisher", ctx.ProductName);
        key.SetValue("InstallLocation", ctx.InstallDir);
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            key.SetValue("DisplayIcon", exePath);

        key.SetValue("NoModify", 0);
        key.SetValue("NoRepair", 1);

        if (File.Exists(uninstallExe))
        {
            key.SetValue("UninstallString", $"\"{uninstallExe}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" /S");
            key.SetValue("ModifyPath", $"\"{uninstallExe}\" /modify");
        }

        try
        {
            File.WriteAllText(
                Path.Combine(ctx.InstallDir, ".flyscreen-install"),
                "setup\n",
                Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    private static void CreateShortcut(string linkPath, string targetPath, string workingDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDir;
        shortcut.Description = description ?? "";
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }
}
