using System.Globalization;

namespace FlyScreen.AppMaui.Converters;

public sealed class PercentToProgressConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return Math.Clamp(d / 100.0, 0, 1);
        if (value is float f) return Math.Clamp(f / 100.0, 0, 1);
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
