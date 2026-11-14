using FlyScreen.Services;
using FlyScreen.Services.Tools;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// Android 等：Tools 根在应用私有目录时，从包内 Raw（Tools/Bundled/*.flytool）种子拷贝。
/// 先走 Core 的 AppBase 种子；再补齐包内仍缺的已知包（含后加的 video-wall）。
/// </summary>
public static class BundledFlyToolsSeeder
{
    public static async Task EnsureAsync(CancellationToken ct = default)
    {
        ToolBundledPackSeeder.TrySeedFromAppBase();

        FlyScreenDataPaths.EnsureToolsLayout();
        var destDir = FlyScreenDataPaths.ToolsBundledDirectory;
        Directory.CreateDirectory(destDir);

        foreach (var name in ToolBundledPackSeeder.KnownPackFileNames)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(destDir, name);
            if (File.Exists(dest))
                continue;

            var logical = $"Tools/Bundled/{name}";
            try
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync(logical).ConfigureAwait(false);
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
