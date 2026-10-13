using System.Windows;
using Media = System.Windows.Media;

namespace FlyScreen.Helpers;

/// <summary>
/// 淡色 / 深色主题：替换窗口资源画刷，配合 DynamicResource 即时生效。
/// </summary>
public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme)
        => string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? theme, FrameworkElement? root = null)
    {
        Current = IsDark(theme) ? Dark : Light;
        var dark = Current == Dark;

        root ??= Application.Current?.MainWindow;
        if (root == null) return;

        var rd = root.Resources;
        Set(rd, "Blue", dark ? "#3B82F6" : "#2563EB");
        Set(rd, "BlueSoft", dark ? "#1E3A5F" : "#DBEAFE");
        Set(rd, "BlueHover", dark ? "#243447" : "#EFF6FF");
        Set(rd, "BluePress", dark ? "#1D4ED8" : "#1D4ED8");
        Set(rd, "Line", dark ? "#334155" : "#E5E7EB");
        Set(rd, "Muted", dark ? "#94A3B8" : "#6B7280");
        Set(rd, "Text", dark ? "#E2E8F0" : "#111827");
        Set(rd, "Success", dark ? "#34D399" : "#059669");
        Set(rd, "WinBg", dark ? "#0F172A" : "#F7F8FA");
        Set(rd, "CardBg", dark ? "#1E293B" : "#FFFFFF");
        Set(rd, "PaneBg", dark ? "#111827" : "#FFFFFF");
        Set(rd, "SubtleBg", dark ? "#1E293B" : "#F3F4F6");
        Set(rd, "ControlBg", dark ? "#0F172A" : "#FFFFFF");
        Set(rd, "ControlBorder", dark ? "#475569" : "#D1D5DB");
        Set(rd, "ControlHover", dark ? "#334155" : "#F9FAFB");
        Set(rd, "Placeholder", dark ? "#64748B" : "#9CA3AF");
        Set(rd, "MatrixBg", dark ? "#0B1220" : "#F8FAFC");
        Set(rd, "ListBg", dark ? "#0B1220" : "#F9FAFB");

        if (root is Window win)
        {
            win.Background = BrushOf(rd, "WinBg");
            win.Foreground = BrushOf(rd, "Text");
        }
    }

    /// <summary>将当前主题应用到子窗口。</summary>
    public static void ApplyToChildWindow(Window child) => Apply(Current, child);

    private static void Set(ResourceDictionary rd, string key, string hex)
    {
        var color = (Media.Color)Media.ColorConverter.ConvertFromString(hex)!;
        rd[key] = new Media.SolidColorBrush(color);
    }

    private static Media.Brush BrushOf(ResourceDictionary rd, string key)
        => rd[key] as Media.Brush ?? Media.Brushes.Transparent;
}
