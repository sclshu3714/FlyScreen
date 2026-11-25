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
            var logical = $"Tools/Bundled/{name}";
            try
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync(logical).ConfigureAwait(false);
                // 包内为权威源：仅「缺失」时跳过会让 Android 私有目录永久卡在旧 .flytool
                // （表现为改了工具代码但日志仍是旧文案）。按长度判断是否需要覆盖。
                if (File.Exists(dest)
                    && src.CanSeek
                    && src.Length > 0
                    && new FileInfo(dest).Length == src.Length)
                    continue;

                if (src.CanSeek)
                    src.Position = 0;
                var staging = dest + ".staging";
                await using (var fs = File.Create(staging))
                    await src.CopyToAsync(fs, ct).ConfigureAwait(false);
                File.Copy(staging, dest, overwrite: true);
                try { File.Delete(staging); } catch { /* ignore */ }
                AppLog.Info($"已从包内刷新 Bundled：{name}");
            }
            catch
            {
                // 包内无此资源时忽略（Windows 旁路拷贝已覆盖）
            }
        }
    }
}
