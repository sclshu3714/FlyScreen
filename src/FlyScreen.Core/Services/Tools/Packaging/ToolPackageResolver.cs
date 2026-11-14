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
        var dir = FlyScreenDataPaths.ToolsBundledDirectory;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.flytool", SearchOption.TopDirectoryOnly))
            yield return f;
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
