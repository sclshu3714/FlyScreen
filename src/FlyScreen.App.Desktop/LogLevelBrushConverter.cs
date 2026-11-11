using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FlyScreen.Services;

namespace FlyScreen.DesktopUi;

/// <summary>日志级别 → 前景色。</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public static readonly LogLevelBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dark = DesktopUi.Helpers.DesktopThemeService.IsDark(DesktopUi.Helpers.DesktopThemeService.Current);
        if (value is not AppLogLevel level)
            return BrushFrom(dark ? "#3B82F6" : "#2563EB");
        return level switch
        {
            AppLogLevel.Error => BrushFrom(dark ? "#F87171" : "#DC2626"),
            AppLogLevel.Warn => BrushFrom(dark ? "#FBBF24" : "#D97706"),
            AppLogLevel.Debug => BrushFrom(dark ? "#94A3B8" : "#6B7280"),
            _ => BrushFrom(dark ? "#3B82F6" : "#2563EB")
        };
    }

    private static IBrush BrushFrom(string hex) => new SolidColorBrush(Color.Parse(hex));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
