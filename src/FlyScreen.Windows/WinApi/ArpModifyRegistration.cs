using System.IO;
using FlyScreen.Services.Update;
using Microsoft.Win32;

namespace FlyScreen.Services.Update;

/// <summary>
/// 已安装包用户：补写 ARP「修改」入口（旧版 NoModify=1 时「修改」为灰）。
/// </summary>
public static class ArpModifyRegistration
{
    public static void EnsureIfInstallerLayout()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (AppInstallLayout.Detect() != AppInstallKind.WindowsInstaller) return;

        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var uninstallExe = Path.Combine(dir, AppInstallLayout.UninstallExeName);
            if (!File.Exists(uninstallExe)) return;

            using var key = Registry.CurrentUser.CreateSubKey(AppInstallLayout.UninstallKeyPath);
            if (key == null) return;

            key.SetValue("NoModify", 0);
            key.SetValue("ModifyPath", $"\"{uninstallExe}\" /modify");
            key.SetValue("InstallLocation", dir);

            var marker = Path.Combine(dir, AppInstallLayout.InstallMarkerFileName);
            if (!File.Exists(marker))
                File.WriteAllText(marker, "setup\n");
        }
        catch
        {
            /* ignore */
        }
    }
}
