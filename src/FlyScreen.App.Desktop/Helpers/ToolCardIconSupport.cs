using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using FlyScreen.Assets;
using FlyScreen.Services.Tools;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>工具卡片图标：颜色拾取器位图与 Id 匹配转换。</summary>
internal static class ToolCardIcons
{
    private static Bitmap? _colorPicker;

    public static Bitmap? ColorPicker
    {
        get
        {
            if (_colorPicker != null) return _colorPicker;
            try
            {
                using var stream = BrandAssets.Open(BrandAssets.ToolColorPickerJpg);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                _colorPicker = new Bitmap(copy);
            }
            catch
            {
                _colorPicker = null;
            }

            return _colorPicker;
        }
    }
}

internal sealed class IsColorPickerToolConverter : IValueConverter
{
    public static readonly IsColorPickerToolConverter Instance = new(invert: false);
    public static readonly IsColorPickerToolConverter NotInstance = new(invert: true);

    private readonly bool _invert;

    private IsColorPickerToolConverter(bool invert) => _invert = invert;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = value is string id
            && string.Equals(id, ToolIds.ColorPicker, StringComparison.OrdinalIgnoreCase);
        return _invert ? !match : match;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
