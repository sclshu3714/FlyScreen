namespace FlyScreen.Uninstaller.Services;

public enum UninstallMode
{
    /// <summary>仅卸载勾选的插件，保留主程序。</summary>
    ToolsOnly = 0,
    /// <summary>卸载主程序，并卸载勾选的插件（先策略后删目录）。</summary>
    Product = 1
}
