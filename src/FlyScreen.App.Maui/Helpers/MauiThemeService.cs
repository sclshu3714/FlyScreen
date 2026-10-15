namespace FlyScreen.AppMaui.Helpers;

/// <summary>
/// MAUI 淡色/深色：覆盖 Colors 资源键，配合 DynamicResource 即时生效。
/// </summary>
public static class MauiThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme)
        => string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? theme, Page? page = null)
    {
        Current = IsDark(theme) ? Dark : Light;
        var dark = Current == Dark;

        var app = Application.Current;
        if (app != null)
            app.UserAppTheme = dark ? AppTheme.Dark : AppTheme.Light;

        Set("Blue", dark ? "#3B82F6" : "#2563EB");
        Set("BlueSoft", dark ? "#1E3A5F" : "#DBEAFE");
        Set("BlueHover", dark ? "#243447" : "#EFF6FF");
        Set("Line", dark ? "#334155" : "#E5E7EB");
        Set("Muted", dark ? "#94A3B8" : "#6B7280");
        Set("Text", dark ? "#E2E8F0" : "#111827");
        Set("Success", dark ? "#34D399" : "#059669");
        Set("WinBg", dark ? "#0F172A" : "#F7F8FA");
        Set("CardBg", dark ? "#1E293B" : "#FFFFFF");
        Set("PaneBg", dark ? "#111827" : "#FFFFFF");
        Set("SubtleBg", dark ? "#1E293B" : "#F3F4F6");
        Set("MatrixBg", dark ? "#0B1220" : "#F8FAFC");
        Set("ListBg", dark ? "#0B1220" : "#F9FAFB");
        Set("Primary", dark ? "#3B82F6" : "#2563EB");

        page ??= TryGetMainPage();
        if (page != null && ColorOf("WinBg") is { } winBg)
            page.BackgroundColor = winBg;
    }

    private static Page? TryGetMainPage()
    {
        var app = Application.Current;
        if (app == null) return null;
        foreach (var window in app.Windows)
        {
            if (window.Page is NavigationPage nav && nav.CurrentPage != null)
                return nav.CurrentPage;
            if (window.Page is Shell shell && shell.CurrentPage != null)
                return shell.CurrentPage;
            if (window.Page != null)
                return window.Page;
        }
        return null;
    }

    private static void Set(string key, string hex)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Resources[key] = Color.FromArgb(hex);
    }

    private static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }
}
