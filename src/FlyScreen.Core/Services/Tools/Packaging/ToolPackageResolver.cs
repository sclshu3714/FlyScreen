using System.Text.Json;

namespace FlyScreen.Services.Tools;

/// <summary>本地源包：仅 <c>Tools/Bundled</c>（内置 + 导入/下载）。</summary>
public static class ToolPackageResolver
{
    public sealed record LocalPack(string Path, string Source);

    /// <summary>查找本地 .flytool（Bundled；文件名可为 id.flytool 或 id-version.flytool）。</summary>
    public static LocalPack? ResolveLocalPack(string toolId, string? preferredFileName = null)
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        toolId = toolId.Trim();

        foreach (var hit in EnumeratePacks(FlyScreenDataPaths.ToolsBundledDirectory, toolId, preferredFileName))
        {
            // 构建内置名为 id.flytool；导入/下载多为 id-version.flytool
            var fn = Path.GetFileName(hit);
            var source = string.Equals(fn, toolId + ".flytool", StringComparison.OrdinalIgnoreCase)
                ? "bundled"
                : "external";
            return new LocalPack(hit, source);
        }

        return null;
    }

    public static IEnumerable<string> EnumerateAllLocalPacks()
    {
        FlyScreenDataPaths.EnsureToolsLayout();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in EnumerateBundledDirs())
        {
            if (!Directory.Exists(dir)) continue;
            RemoveRedundantVersionedPacks(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*.flytool", SearchOption.TopDirectoryOnly))
            {
                // 同名包优先 ToolsRoot/Bundled；程序旁与 ToolsRoot 相同时不会重复
                var key = Path.GetFileName(f);
                if (!seen.Add(key)) continue;
                // 已有 id.flytool 时跳过 id-version.flytool（避免卡片探测/列表重复）
                if (IsVersionedPackFileName(key) && HasCanonicalPackBeside(f))
                    continue;
                yield return f;
            }
        }
    }

    /// <summary>
    /// 若目录内同时存在 <c>id.flytool</c> 与 <c>id-*.flytool</c>，删除后者，避免重复包。
    /// </summary>
    public static int RemoveRedundantVersionedPacks(string? dir = null)
    {
        dir ??= FlyScreenDataPaths.ToolsBundledDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return 0;

        var files = Directory.GetFiles(dir, "*.flytool", SearchOption.TopDirectoryOnly);
        if (files.Length < 2) return 0;

        var removed = 0;
        foreach (var path in files)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem)) continue;

            // 找更短的「规范名」：存在 foo.flytool 且本文件为 foo-….flytool
            foreach (var other in files)
            {
                if (string.Equals(other, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                var otherStem = Path.GetFileNameWithoutExtension(other);
                if (string.IsNullOrEmpty(otherStem)) continue;
                if (!stem.StartsWith(otherStem + "-", StringComparison.OrdinalIgnoreCase))
                    continue;
                // other 必须是规范名文件 otherStem.flytool（不能是又一版带后缀的）
                if (!string.Equals(Path.GetFileName(other), otherStem + ".flytool", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(path);
                    removed++;
                    AppLog.Info($"已删除重复工具包 {Path.GetFileName(path)}（保留 {Path.GetFileName(other)}）");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"删除重复工具包失败 {path}: {ex.Message}");
                }

                break;
            }
        }

        return removed;
    }

    /// <summary><c>video-wall-0.1.0.flytool</c> 这类带版本后缀的文件名。</summary>
    public static bool IsVersionedPackFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        if (string.IsNullOrEmpty(stem)) return false;
        var dash = stem.LastIndexOf('-');
        if (dash <= 0 || dash >= stem.Length - 1) return false;
        // 后缀看起来像版本号（含点或纯数字段），避免误伤尚未拆出的 id
        var suffix = stem[(dash + 1)..];
        return suffix.Any(char.IsDigit) && (suffix.Contains('.') || suffix.All(c => char.IsDigit(c) || c == '.'));
    }

    private static bool HasCanonicalPackBeside(string versionedPackPath)
    {
        var stem = Path.GetFileNameWithoutExtension(versionedPackPath);
        if (string.IsNullOrEmpty(stem)) return false;
        var dir = Path.GetDirectoryName(versionedPackPath);
        if (string.IsNullOrEmpty(dir)) return false;
        var dash = stem.LastIndexOf('-');
        if (dash <= 0) return false;
        // 尝试所有可能的规范前缀：video-wall-0.1.0 → video-wall / video
        for (var i = dash; i > 0; i = stem.LastIndexOf('-', i - 1))
        {
            var candidate = stem[..i] + ".flytool";
            if (File.Exists(Path.Combine(dir, candidate)))
                return true;
            if (i <= 1) break;
        }

        return false;
    }

    /// <summary>ToolsRoot/Bundled（桌面即 exe 旁）；与 BaseDirectory 不一致时再扫一份种子源。</summary>
    private static IEnumerable<string> EnumerateBundledDirs()
    {
        yield return FlyScreenDataPaths.ToolsBundledDirectory;
        var baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var beside = Path.Combine(baseDir, "Tools", "Bundled");
        if (!PathsEqual(beside, FlyScreenDataPaths.ToolsBundledDirectory))
            yield return beside;
    }

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

    public static async Task<ToolPackageManifest?> ReadManifestFromPackAsync(string flytoolPath, CancellationToken ct = default)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(flytoolPath);
            var entry = zip.GetEntry("manifest.json")
                        ?? zip.Entries.FirstOrDefault(e =>
                            e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                            || e.FullName.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            await using var s = entry.Open();
            return await JsonSerializer.DeserializeAsync<ToolPackageManifest>(s, ToolPackageJson.Options, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 .flytool manifest 失败: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> EnumeratePacks(string dir, string toolId, string? preferredFileName)
    {
        if (!Directory.Exists(dir)) yield break;

        if (!string.IsNullOrWhiteSpace(preferredFileName))
        {
            var preferred = Path.Combine(dir, preferredFileName);
            if (File.Exists(preferred)) yield return preferred;
        }

        var byName = Path.Combine(dir, toolId + ".flytool");
        if (File.Exists(byName)) yield return byName;

        foreach (var f in Directory.EnumerateFiles(dir, "*.flytool", SearchOption.TopDirectoryOnly))
        {
            var fn = Path.GetFileNameWithoutExtension(f);
            if (fn.StartsWith(toolId, StringComparison.OrdinalIgnoreCase)
                || fn.Equals(toolId, StringComparison.OrdinalIgnoreCase))
                yield return f;
        }
    }
}
