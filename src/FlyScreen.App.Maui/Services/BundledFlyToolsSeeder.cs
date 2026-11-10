using FlyScreen.Services;
using FlyScreen.Services.Tools;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// Android 等：Tools 根在应用私有目录时，从包内 Raw（Tools/Bundled/*.flytool）种子拷贝。
/// 桌面旁路已有文件则跳过；并先走 Core 的 AppBase 种子（与 Win/Desktop 对齐）。
/// </summary>
public static class BundledFlyToolsSeeder
{
    public static async Task EnsureAsync(CancellationToken ct = default)
    {
        ToolBundledPackSeeder.TrySeedFromAppBase();

        FlyScreenDataPaths.EnsureToolsLayout();
        var destDir = FlyScreenDataPaths.ToolsBundledDirectory;
        Directory.CreateDirectory(destDir);

        // 桌面旁路 / AppBase 种子已有文件则跳过
        if (Directory.EnumerateFiles(destDir, "*.flytool").Any())
            return;

        foreach (var name in new[] { "color-picker.flytool", "shared-devices.flytool", "tools-index.json" })
        {
            ct.ThrowIfCancellationRequested();
            var logical = $"Tools/Bundled/{name}";
            try
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync(logical).ConfigureAwait(false);
                var dest = Path.Combine(destDir, name);
                await using var fs = File.Create(dest);
                await src.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            catch
            {
                // 包内无此资源时忽略（Windows 旁路拷贝已覆盖）
            }
        }
    }
}
