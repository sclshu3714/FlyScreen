using FlyScreen.Assets;
using FlyScreen.Models;

namespace FlyScreen.Services;

/// <summary>
/// 主界面背景图：优先拷入<strong>安装目录</strong>（exe 旁 <c>Ui/Wallpaper</c>）；
/// 无写权限时回退 AppData。清除时两边都会删。
/// 自定义主题无用户图时种入嵌入的 <see cref="DefaultFileName"/>。
/// </summary>
public static class UiWallpaperStore
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    public const string RelativeDir = "Ui/Wallpaper";
    public const string DefaultFileName = "bg_default.png";
    public const string DefaultRelativePath = RelativeDir + "/" + DefaultFileName;

    public static string InstallRoot =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string AppDataDirectoryPath =>
        Path.Combine(FlyScreenDataPaths.Root, "Ui", "Wallpaper");

    private static string InstallDirectoryPath =>
        Path.Combine(InstallRoot, "Ui", "Wallpaper");

    /// <summary>当前可写壁纸目录（探测后缓存到实际写入位置）。</summary>
    public static string DirectoryPath =>
        FlyScreenDataPaths.HasOverride ? AppDataDirectoryPath : InstallDirectoryPath;

    /// <summary>
    /// 切入/应用自定义主题时的壁纸策略：
    /// 刚切入自定义 → 取消 cleared，无有效用户图则种默认图；
    /// 已清除且仍停在自定义 → 保持无图；
    /// 有用户图 → 保留。
    /// </summary>
    /// <param name="supportsWallpaper">新主题是否支持壁纸（自定义）。</param>
    /// <param name="switchedOntoWallpaperTheme">是否从非壁纸主题刚切到壁纸主题。</param>
    public static void ApplyThemePolicy(
        AppSettings settings,
        bool supportsWallpaper,
        bool switchedOntoWallpaperTheme)
    {
        if (!supportsWallpaper)
            return;

        if (switchedOntoWallpaperTheme)
            settings.UiWallpaperCleared = false;

        if (settings.UiWallpaperCleared)
        {
            settings.UiWallpaperPath = "";
            return;
        }

        if (!string.IsNullOrWhiteSpace(settings.UiWallpaperPath)
            && ResolveExisting(settings.UiWallpaperPath) != null)
            return;

        settings.UiWallpaperPath = EnsureDefault();
    }

    /// <summary>种入嵌入默认图并返回相对路径。</summary>
    public static string EnsureDefault()
    {
        SeedDefaultFile();
        return DefaultRelativePath;
    }

    /// <summary>从 Assets 嵌入资源写出 <c>bg_default.png</c>（已存在且非空则跳过）。</summary>
    public static void SeedDefaultFile()
    {
        var candidates = FlyScreenDataPaths.HasOverride
            ? new[] { AppDataDirectoryPath }
            : new[] { InstallDirectoryPath, AppDataDirectoryPath };

        Exception? last = null;
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (!CanWriteProbe(dir))
                {
                    last = new UnauthorizedAccessException("目录不可写: " + dir);
                    continue;
                }

                var dest = Path.Combine(dir, DefaultFileName);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                    return;

                using var src = BrandAssets.Open(BrandAssets.DefaultWallpaperPng);
                using var dst = File.Create(dest);
                src.CopyTo(dst);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last != null)
            throw last;
    }

    /// <summary>
    /// 导入图片。成功返回相对路径；失败返回 null，<paramref name="error"/> 为人可读原因。
    /// </summary>
    public static string? Import(string sourcePath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            error = "源文件不存在";
            return null;
        }

        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExt.Contains(ext))
        {
            error = "不支持的图片格式";
            return null;
        }

        var destName = "wallpaper" + ext.ToLowerInvariant();
        var candidates = FlyScreenDataPaths.HasOverride
            ? new[] { AppDataDirectoryPath }
            : new[] { InstallDirectoryPath, AppDataDirectoryPath };

        Exception? last = null;
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (!CanWriteProbe(dir))
                {
                    last = new UnauthorizedAccessException("目录不可写: " + dir);
                    continue;
                }

                RemoveOtherUserWallpaperFiles(dir, destName);
                var dest = Path.Combine(dir, destName);
                File.Copy(sourcePath, dest, overwrite: true);
                return $"{RelativeDir}/{destName}";
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        error = last?.Message ?? "无法写入壁纸目录";
        return null;
    }

    /// <summary>兼容旧调用。</summary>
    public static string? Import(string sourcePath) => Import(sourcePath, out _);

    public static string? ResolveExisting(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        var raw = stored.Trim();
        if (Path.IsPathRooted(raw))
            return File.Exists(raw) ? raw : null;

        var rel = raw.Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in new[] { InstallRoot, FlyScreenDataPaths.Root })
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // ignore
            }
        }

        // 默认图：路径指向默认但磁盘没有时再种一次
        if (string.Equals(
                raw.Replace('\\', '/'),
                DefaultRelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                SeedDefaultFile();
                foreach (var root in new[] { InstallRoot, FlyScreenDataPaths.Root })
                {
                    var full = Path.GetFullPath(Path.Combine(root, rel));
                    if (File.Exists(full))
                        return full;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    public static void ClearFiles()
    {
        ClearDirectoryFiles(InstallDirectoryPath);
        ClearDirectoryFiles(AppDataDirectoryPath);
    }

    /// <summary>清除用户选择的 wallpaper.*，保留默认 bg_default（若存在）。</summary>
    private static void RemoveOtherUserWallpaperFiles(string dir, string keepFileName)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "wallpaper.*"))
            {
                if (string.Equals(Path.GetFileName(f), keepFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool CanWriteProbe(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write_probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearDirectoryFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(f); } catch { /* ignore locked */ }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
                // ignore
            }
        }
        catch
        {
            // ignore
        }
    }

    public static string DisplayName(string? stored)
    {
        var full = ResolveExisting(stored);
        if (full == null) return "";
        return Path.GetFileName(full);
    }
}
