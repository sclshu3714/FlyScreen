using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FlyScreen.Services;

namespace FlyScreen.DesktopUi;

/// <summary>日志级别 → 前景色。</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public static readonly LogLevelBrushConverter Instance = new();

    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Debug = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#2563EB"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppLogLevel level
            ? level switch
            {
                AppLogLevel.Error => Error,
                AppLogLevel.Warn => Warn,
                AppLogLevel.Debug => Debug,
                _ => Info
            }
            : Info;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
