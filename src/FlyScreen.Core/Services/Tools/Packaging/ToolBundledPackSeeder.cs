namespace FlyScreen.Services.Tools;

/// <summary>
/// 当 <see cref="FlyScreenDataPaths.ToolsBundledDirectory"/> 尚无 .flytool 时，
/// 从程序目录旁的 <c>Tools/Bundled</c> 种子拷贝（对齐 Maui <c>BundledFlyToolsSeeder</c>）。
/// 覆盖：安装到不可写目录后 ToolsRoot 回落到 AppData、或旁路目录曾为空后补齐。
/// </summary>
public static class ToolBundledPackSeeder
{
    private static readonly string[] PackFileNames =
    [
        "color-picker.flytool",
        "shared-devices.flytool",
        "port-proxy.flytool",
        "tools-index.json"
    ];

    public static void TrySeedFromAppBase()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var destDir = FlyScreenDataPaths.ToolsBundledDirectory;
        Directory.CreateDirectory(destDir);
        if (HasFlyTools(destDir))
            return;

        foreach (var sourceDir in EnumerateSourceCandidates())
        {
            if (!Directory.Exists(sourceDir))
                continue;
            if (PathsEqual(sourceDir, destDir))
                continue;
            if (!HasFlyTools(sourceDir))
                continue;

            try
            {
                CopyPacks(sourceDir, destDir);
                AppLog.Info($"已种子拷贝 Bundled flytool ← {sourceDir}");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Bundled flytool 种子拷贝失败 ← {sourceDir}: {ex.Message}");
            }
        }
    }

    /// <summary>由宿主提供已打开的包内流（Maui Raw 等）。</summary>
    public static void TrySeedFromStreams(IEnumerable<(string FileName, Stream Content)> packs)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var destDir = FlyScreenDataPaths.ToolsBundledDirectory;
        Directory.CreateDirectory(destDir);
        if (HasFlyTools(destDir))
            return;

        foreach (var (fileName, content) in packs)
        {
            if (string.IsNullOrWhiteSpace(fileName) || content == null)
                continue;
            try
            {
                var dest = Path.Combine(destDir, Path.GetFileName(fileName));
                using var fs = File.Create(dest);
                content.CopyTo(fs);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Bundled 流种子失败 {fileName}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceCandidates()
    {
        var list = new List<string>();
        var baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        list.Add(Path.Combine(baseDir, "Tools", "Bundled"));

        // 单文件/某些宿主下 BaseDirectory 可能指向临时解压根，再试程序集旁
        try
        {
            var asm = typeof(ToolBundledPackSeeder).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(asm))
            {
                var asmDir = Path.GetDirectoryName(asm);
                if (!string.IsNullOrWhiteSpace(asmDir))
                    list.Add(Path.Combine(asmDir, "Tools", "Bundled"));
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static void CopyPacks(string sourceDir, string destDir)
    {
        foreach (var name in PackFileNames)
        {
            var src = Path.Combine(sourceDir, name);
            if (!File.Exists(src))
                continue;
            var dest = Path.Combine(destDir, name);
            File.Copy(src, dest, overwrite: false);
        }

        // 兼容额外命名：id-version.flytool
        foreach (var src in Directory.EnumerateFiles(sourceDir, "*.flytool", SearchOption.TopDirectoryOnly))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(src));
            if (!File.Exists(dest))
                File.Copy(src, dest, overwrite: false);
        }
    }

    private static bool HasFlyTools(string dir)
        => Directory.Exists(dir)
           && Directory.EnumerateFiles(dir, "*.flytool", SearchOption.TopDirectoryOnly).Any();

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
