namespace FlyScreen.Services;

/// <summary>
/// 主界面背景图：优先拷入<strong>安装目录</strong>（exe 旁 <c>Ui/Wallpaper</c>）；
/// 无写权限时回退 AppData。清除时两边都会删。
/// </summary>
public static class UiWallpaperStore
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    public const string RelativeDir = "Ui/Wallpaper";

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

                RemoveOtherWallpaperFiles(dir, destName);
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

        return null;
    }

    public static void ClearFiles()
    {
        ClearDirectoryFiles(InstallDirectoryPath);
        ClearDirectoryFiles(AppDataDirectoryPath);
    }

    private static void RemoveOtherWallpaperFiles(string dir, string keepFileName)
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
