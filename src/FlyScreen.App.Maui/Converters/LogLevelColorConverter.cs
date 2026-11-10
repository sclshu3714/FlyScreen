using System.Globalization;
using FlyScreen.Services;

namespace FlyScreen.AppMaui.Converters;

/// <summary>日志级别 → 文字颜色。</summary>
public sealed class LogLevelColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppLogLevel level
            ? level switch
            {
                AppLogLevel.Error => Color.FromArgb("#DC2626"),
                AppLogLevel.Warn => Color.FromArgb("#D97706"),
                AppLogLevel.Debug => Color.FromArgb("#6B7280"),
                _ => Color.FromArgb("#2563EB")
            }
            : Color.FromArgb("#6B7280");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
