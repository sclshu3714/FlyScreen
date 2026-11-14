namespace FlyScreen.Services.Tools;

/// <summary>
/// 将程序目录旁的 <c>Tools/Bundled</c> 种子拷贝到 <see cref="FlyScreenDataPaths.ToolsBundledDirectory"/>
/// （对齐 Maui <c>BundledFlyToolsSeeder</c>）。
/// 桌面 ToolsRoot 即 exe 旁；以及「目录已有旧包、但缺新工具包」时补拷（如后加的 video-wall）。
/// </summary>
public static class ToolBundledPackSeeder
{
    private static readonly string[] PackFileNames =
    [
        "color-picker.flytool",
        "shared-devices.flytool",
        "port-proxy.flytool",
        "video-wall.flytool",
        "tools-index.json"
    ];

    public static void TrySeedFromAppBase()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var destDir = FlyScreenDataPaths.ToolsBundledDirectory;
        Directory.CreateDirectory(destDir);

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
                var copied = CopyMissingPacks(sourceDir, destDir);
                if (copied > 0)
                    AppLog.Info($"已种子补齐 Bundled flytool ×{copied} ← {sourceDir}");
                // 已从某一源补齐；若仍缺包可继续试下一候选
                if (!HasMissingKnownPacks(destDir))
                    break;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Bundled flytool 种子拷贝失败 ← {sourceDir}: {ex.Message}");
            }
        }

        ToolPackageResolver.RemoveRedundantVersionedPacks(destDir);
    }

    /// <summary>由宿主提供已打开的包内流（Maui Raw 等）。</summary>
    public static void TrySeedFromStreams(IEnumerable<(string FileName, Stream Content)> packs)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var destDir = FlyScreenDataPaths.ToolsBundledDirectory;
        Directory.CreateDirectory(destDir);

        foreach (var (fileName, content) in packs)
        {
            if (string.IsNullOrWhiteSpace(fileName) || content == null)
                continue;
            try
            {
                var dest = Path.Combine(destDir, Path.GetFileName(fileName));
                if (File.Exists(dest))
                    continue;
                using var fs = File.Create(dest);
                content.CopyTo(fs);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Bundled 流种子失败 {fileName}: {ex.Message}");
            }
        }
    }

    /// <summary>已知内置包文件名（供 Maui Raw 种子等枚举）。</summary>
    public static IReadOnlyList<string> KnownPackFileNames => PackFileNames;

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

    /// <returns>新拷贝或覆盖更新的文件数。</returns>
    private static int CopyMissingPacks(string sourceDir, string destDir)
    {
        var copied = 0;
        foreach (var name in PackFileNames)
        {
            var src = Path.Combine(sourceDir, name);
            if (!File.Exists(src))
                continue;
            var dest = Path.Combine(destDir, name);
            if (ShouldCopyPack(src, dest))
            {
                File.Copy(src, dest, overwrite: true);
                copied++;
            }
        }

        // 其它 *.flytool：仅拷规范名；已有 id.flytool 时不再拷 id-version.flytool
        foreach (var src in Directory.EnumerateFiles(sourceDir, "*.flytool", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(src);
            if (PackFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                continue;
            if (ToolPackageResolver.IsVersionedPackFileName(fileName))
            {
                // 源/目标任一侧已有规范名则跳过版本后缀包
                var stem = Path.GetFileNameWithoutExtension(fileName)!;
                if (HasAnyCanonicalForVersioned(stem, sourceDir) || HasAnyCanonicalForVersioned(stem, destDir))
                    continue;
            }

            var dest = Path.Combine(destDir, fileName);
            if (!ShouldCopyPack(src, dest))
                continue;
            File.Copy(src, dest, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static bool HasAnyCanonicalForVersioned(string versionedStem, string dir)
    {
        if (!Directory.Exists(dir)) return false;
        for (var i = versionedStem.LastIndexOf('-'); i > 0; i = versionedStem.LastIndexOf('-', i - 1))
        {
            if (File.Exists(Path.Combine(dir, versionedStem[..i] + ".flytool")))
                return true;
            if (i <= 1) break;
        }

        return false;
    }

    /// <summary>目标不存在，或源文件更新（按写入时间）时拷贝。</summary>
    private static bool ShouldCopyPack(string src, string dest)
    {
        if (!File.Exists(dest))
            return true;
        try
        {
            return File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMissingKnownPacks(string destDir)
    {
        foreach (var name in PackFileNames)
        {
            if (!name.EndsWith(".flytool", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(Path.Combine(destDir, name)))
                return true;
        }

        return false;
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
