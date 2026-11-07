namespace FlyScreen.Services.Update;

/// <summary>本机安装形态：影响检查更新时优先匹配的 Release 资源与安装方式。</summary>
public enum AppInstallKind
{
    /// <summary>单文件 / 绿色目录发布，自替换 exe。</summary>
    Standalone,

    /// <summary>Windows 自定义安装包（含 Uninstall.exe / 安装标记）。</summary>
    WindowsInstaller
}

public static class AppInstallLayout
{
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FlyScreen";
    public const string InstallMarkerFileName = ".flyscreen-install";
    public const string UninstallExeName = "Uninstall.exe";

    /// <summary>
    /// 检测当前进程是否由安装包安装。
    /// 依据：安装目录标记文件，或同目录存在 Uninstall.exe（与主程序并列）。
    /// </summary>
    public static AppInstallKind Detect(string? processPath = null)
    {
        if (!OperatingSystem.IsWindows())
            return AppInstallKind.Standalone;

        try
        {
            processPath ??= Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return AppInstallKind.Standalone;

            var dir = Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (string.IsNullOrWhiteSpace(dir))
                return AppInstallKind.Standalone;

            if (File.Exists(Path.Combine(dir, InstallMarkerFileName)))
                return AppInstallKind.WindowsInstaller;

            // 安装包布局：主程序旁有 Uninstall.exe（单文件发布通常没有）
            var uninstall = Path.Combine(dir, UninstallExeName);
            var mainName = Path.GetFileName(processPath);
            if (File.Exists(uninstall)
                && !mainName.Equals(UninstallExeName, StringComparison.OrdinalIgnoreCase)
                && !mainName.StartsWith("FlyScreen-Uninstall", StringComparison.OrdinalIgnoreCase))
                return AppInstallKind.WindowsInstaller;
        }
        catch
        {
            /* ignore */
        }

        return AppInstallKind.Standalone;
    }

    public static bool PreferInstallerPackageAsset()
        => Detect() == AppInstallKind.WindowsInstaller;
}
