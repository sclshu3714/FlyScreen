using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Services.Tools;

namespace FlyScreen.DesktopUi;

/// <summary>工具卡片 VisualState → 主题底色画刷。</summary>
public sealed class ToolCardBgConverter : IValueConverter
{
    public static readonly ToolCardBgConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dark = DesktopThemeService.IsDark(DesktopThemeService.Current);
        var state = value as string ?? ToolVisualStates.NotInstalled;
        var hex = state switch
        {
            ToolVisualStates.Running => dark ? "#1E3A5F" : "#DBEAFE",
            ToolVisualStates.Installed => dark ? "#0F3D2E" : "#ECFDF5",
            ToolVisualStates.LocalProgram => dark ? "#0F3D2E" : "#ECFDF5",
            ToolVisualStates.Installing => dark ? "#243447" : "#EFF6FF",
            _ => dark ? "#1E293B" : "#F3F4F6"
        };
        return Brush.Parse(hex);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
