using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyScreen.Services.Tools;

namespace FlyScreen.Helpers;

/// <summary>从 <see cref="ToolIconRegistry"/> 生成 WPF <see cref="ImageSource"/>。</summary>
internal static class ToolIconImageFactory
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryGet(string? iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey)) return null;
        var key = iconKey.Trim();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
                return hit;
        }

        ToolBundledCatalogAssets.Ensure();
        ToolIconRegistry.EnsureFromLoadedAssemblies();
        var bytes = ToolIconRegistry.TryReadBitmapBytes(key);
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            lock (Cache) Cache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearCache()
    {
        lock (Cache) Cache.Clear();
    }
}
