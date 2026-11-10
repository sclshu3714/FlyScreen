using System.Globalization;
using System.Windows.Data;

namespace FlyScreen.Helpers;

/// <summary>IconKey → 工具程序集贡献的位图。</summary>
public sealed class ToolIconKeyToImageConverter : IValueConverter
{
    public static readonly ToolIconKeyToImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ToolIconImageFactory.TryGet(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>有位图则 Visible，否则 Collapsed（用于回退矢量）。</summary>
public sealed class ToolIconKeyHasImageConverter : IValueConverter
{
    public static readonly ToolIconKeyHasImageConverter Instance = new();
    public static readonly ToolIconKeyHasImageConverter InvertInstance = new() { Invert = true };

    public bool Invert { get; init; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = ToolIconImageFactory.TryGet(value as string) != null;
        var visible = Invert ? !has : has;
        return visible
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
