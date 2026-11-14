using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using FlyScreen.Services.Tools;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>从 <see cref="ToolIconRegistry"/> 解析工具卡片/快捷栏位图。</summary>
internal static class ToolCardIcons
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? TryGet(string? iconKey)
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
            var bmp = new Bitmap(ms);
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

/// <summary>IconKey → Bitmap（工具程序集贡献）。</summary>
internal sealed class ToolIconKeyToBitmapConverter : IValueConverter
{
    public static readonly ToolIconKeyToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ToolCardIcons.TryGet(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
