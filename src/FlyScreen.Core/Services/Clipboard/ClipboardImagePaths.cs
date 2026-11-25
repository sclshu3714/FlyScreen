namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 判断剪贴板路径是否为常见图片文件（用于「复制图片」→ 文件宣告时补内联位图）。
/// </summary>
public static class ClipboardImagePaths
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif",
        ".gif", ".bmp", ".dib", ".webp",
        ".tif", ".tiff", ".heic", ".heif", ".avif"
    };

    public static bool IsImageFileName(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return false;
        var ext = Path.GetExtension(nameOrPath.Trim());
        return ext.Length > 0 && ImageExtensions.Contains(ext);
    }

    /// <summary>全部为图片文件（无文件夹、无非图文件）时，对端应同时挂位图 + 文件路径。</summary>
    public static bool AreAllImageFiles(IEnumerable<(string Name, bool IsDirectory)> entries)
    {
        var any = false;
        foreach (var (name, isDirectory) in entries)
        {
            any = true;
            if (isDirectory || !IsImageFileName(name))
                return false;
        }

        return any;
    }

    public static bool AreAllImageFilePaths(IEnumerable<string> paths)
    {
        var any = false;
        foreach (var path in paths)
        {
            any = true;
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (Directory.Exists(path)) return false;
            if (!IsImageFileName(path)) return false;
        }

        return any;
    }
}
