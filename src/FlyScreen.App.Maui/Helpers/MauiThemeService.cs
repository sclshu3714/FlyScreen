using FlyScreen.Ui.Theme;
using FlyScreen.Ui.Theme.Maui;

namespace FlyScreen.AppMaui.Helpers;

/// <summary>
/// MAUI 界面主题：按 <see cref="ThemePalette"/> 已注册主题覆写 Colors 资源键；
/// Win 上同步 RequestedTheme / 标题栏，并强制刷新 RadioButton 前景。
/// </summary>
public static class MauiThemeService
{
    public const string Light = ThemePalette.LightName;
    public const string Dark = ThemePalette.DarkName;

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme) => ThemePalette.IsDark(theme);

    public static void Apply(string? theme, Page? page = null)
    {
        var info = ThemePalette.Resolve(theme);
        Current = info.Id;
        var dark = info.IsDark;

        var app = Application.Current;
        if (app != null)
            app.UserAppTheme = dark ? AppTheme.Dark : AppTheme.Light;

        if (app?.Resources != null)
            MauiTheme.ApplyColors(app.Resources, info.ResolveColors(maui: true));

        page ??= TryGetMainPage();
        if (page != null && ColorOf("WinBg") is { } winBg)
            page.BackgroundColor = winBg;

        MauiScrollChrome.Apply(dark);
#if WINDOWS
        Platforms.Windows.MauiWindowChrome.ApplyTheme(dark);
#endif

        MauiRadioTheme.RefreshAll(page);
        if (page != null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MauiRadioTheme.RefreshAll(page);
#if WINDOWS
                Platforms.Windows.MauiWindowChrome.ApplyTheme(dark);
#endif
            });
            _ = page.Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(50);
                MauiRadioTheme.RefreshAll(page);
                await Task.Delay(150);
                MauiRadioTheme.RefreshAll(page);
            });
        }
    }

    public static void RefreshRadioButtonsInVisualTree(Element? root)
        => MauiRadioTheme.RefreshAll(root);

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

    private static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }
}
