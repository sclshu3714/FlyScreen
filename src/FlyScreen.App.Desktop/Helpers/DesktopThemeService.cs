using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// Avalonia 淡色/深色：覆盖 WinParity 画刷，并切换 Fluent RequestedThemeVariant。
/// </summary>
public static class DesktopThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme)
        => string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? theme, Window? window = null)
    {
        Current = IsDark(theme) ? Dark : Light;
        var dark = Current == Dark;

        var app = Application.Current;
        if (app != null)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        Set("Blue", dark ? "#3B82F6" : "#2563EB");
        Set("BlueSoft", dark ? "#1E3A5F" : "#DBEAFE");
        Set("BlueHover", dark ? "#243447" : "#EFF6FF");
        Set("BluePress", dark ? "#1D4ED8" : "#1D4ED8");
        Set("Line", dark ? "#334155" : "#E5E7EB");
        Set("Muted", dark ? "#94A3B8" : "#6B7280");
        Set("Text", dark ? "#E2E8F0" : "#111827");
        Set("Success", dark ? "#34D399" : "#059669");
        Set("Warn", dark ? "#FBBF24" : "#D97706");
        Set("WarnSoft", dark ? "#422006" : "#FEF3C7");
        Set("WinBg", dark ? "#0F172A" : "#F7F8FA");
        Set("CardBg", dark ? "#1E293B" : "#FFFFFF");
        Set("PaneBg", dark ? "#111827" : "#FFFFFF");
        Set("SubtleBg", dark ? "#1E293B" : "#F3F4F6");
        Set("ControlBg", dark ? "#0F172A" : "#FFFFFF");
        Set("ControlBorder", dark ? "#475569" : "#D1D5DB");
        Set("ControlHover", dark ? "#334155" : "#F9FAFB");
        Set("Placeholder", dark ? "#64748B" : "#9CA3AF");
        Set("MatrixBg", dark ? "#0B1220" : "#F8FAFC");
        Set("ListBg", dark ? "#0B1220" : "#F9FAFB");

        window ??= TryGetMainWindow();
        if (window == null) return;

        window.Foreground = Brush("Text") ?? window.Foreground;
        // 自绘圆角窗（Background 透明）时由 WindowChrome 铺色，勿覆盖透明
        if (window.Background is not SolidColorBrush { Color.A: 0 })
            window.Background = Brush("WinBg") ?? window.Background;

        window.InvalidateVisual();
    }

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow;
        return null;
    }

    private static void Set(string key, string hex)
    {
        var app = Application.Current;
        if (app == null) return;

        var color = Color.Parse(hex);

        // 优先原地改色，已绑定的 DynamicResource 会跟着变
        if (TryFindBrush(app, key, out var brush))
        {
            brush.Color = color;
            app.Resources[key] = brush;
            return;
        }

        app.Resources[key] = new SolidColorBrush(color);
    }

    private static bool TryFindBrush(Application app, string key, out SolidColorBrush brush)
    {
        brush = null!;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var r) && r is SolidColorBrush b)
        {
            brush = b;
            return true;
        }

        if (app.TryGetResource(key, app.ActualThemeVariant, out r) && r is SolidColorBrush b2)
        {
            brush = b2;
            return true;
        }

        return false;
    }

    private static IBrush? Brush(string key)
    {
        var app = Application.Current;
        if (app == null) return null;
        if (app.TryGetResource(key, app.ActualThemeVariant, out var r) && r is IBrush b)
            return b;
        return null;
    }
}
