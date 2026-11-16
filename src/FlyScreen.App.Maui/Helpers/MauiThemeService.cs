using FlyScreen.Services;
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
    public const string Custom = ThemePalette.CustomName;

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme) => ThemePalette.IsDark(theme);

    public static void Apply(string? theme, Page? page = null, string? wallpaperPath = null)
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
        var winBg = ColorOf("WinBg");
        var resolved = info.SupportsWallpaper ? UiWallpaperStore.ResolveExisting(wallpaperPath) : null;
        if (page != null)
        {
            if (resolved != null)
                page.BackgroundColor = Colors.Transparent;
            else if (info.SupportsWallpaper)
                // 自定义无图：实心浅色衬底，半透明侧栏/卡片才不会衬黑
                page.BackgroundColor = Color.FromArgb("#F7F8FA");
            else if (winBg is { } bg)
                page.BackgroundColor = bg;
        }

        if (page is IWallpaperHost host)
            host.ApplyWallpaper(resolved);

        MauiScrollChrome.Apply(dark);
#if WINDOWS
        Platforms.Windows.MauiWindowChrome.ApplyTheme(dark, translucent: info.SupportsWallpaper);
#endif

        MauiRadioTheme.RefreshAll(page);
        RefreshButtonChrome(page);
        if (page != null)
        {
            var translucent = info.SupportsWallpaper;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MauiRadioTheme.RefreshAll(page);
                RefreshButtonChrome(page);
#if WINDOWS
                Platforms.Windows.MauiWindowChrome.ApplyTheme(dark, translucent: translucent);
#endif
            });
            _ = page.Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(50);
                MauiRadioTheme.RefreshAll(page);
                RefreshButtonChrome(page);
#if WINDOWS
                Platforms.Windows.MauiWindowChrome.ApplyTheme(dark, translucent: translucent);
#endif
                await Task.Delay(150);
                MauiRadioTheme.RefreshAll(page);
                RefreshButtonChrome(page);
#if WINDOWS
                Platforms.Windows.MauiWindowChrome.ApplyTheme(dark, translucent: translucent);
#endif
            });
        }
    }

    public static void RefreshRadioButtonsInVisualTree(Element? root)
        => MauiRadioTheme.RefreshAll(root);

    /// <summary>
    /// 清掉 VisualState 残留的本地 BackgroundColor/TextColor，再进 Normal，
    /// 否则切淡色后「退出」等 NormalBtn 仍可能发黑看不清。
    /// </summary>
    public static void RefreshButtonChrome(Element? root)
    {
        if (root == null) return;
        if (root is Button btn)
        {
            btn.ClearValue(Button.BackgroundColorProperty);
            btn.ClearValue(Button.TextColorProperty);
            btn.ClearValue(Button.BorderColorProperty);
            VisualStateManager.GoToState(btn, "Normal");
        }

        if (root is not IVisualTreeElement tree) return;
        foreach (var child in tree.GetVisualChildren())
        {
            if (child is Element el)
                RefreshButtonChrome(el);
        }
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

    private static Color? ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }
}

/// <summary>主页实现壁纸层；其它 Page 可忽略。</summary>
public interface IWallpaperHost
{
    void ApplyWallpaper(string? absolutePath);
}
