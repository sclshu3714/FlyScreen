using FlyScreen.Services;
using FlyScreen.Ui.Maui;
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
        MauiDialogShell.ActiveThemeId = info.Id;
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

    /// <summary>遮罩弹层：ForDialog 实心 + 壳外全透明。</summary>
    public static void PrepareOverlayPage(ContentPage page)
    {
        ApplyToDialog(page);
        MauiDialogShell.PrepareOverlayPage(page, Current);
    }

    /// <summary>
    /// 模态页/子页：自定义主题半透明色只给主页透壁纸；弹层用实心表面，避免透出下层页面。
    /// MAUI 的 DynamicResource 常落到 Application，故除写 Page.Resources 外还强制覆写视觉树本地背景。
    /// </summary>
    public static void ApplyToDialog(Page page)
    {
        var colors = ThemePalette.ForDialog(Current, maui: true);
        MauiTheme.ApplyColors(page.Resources, colors);
        var winBg = Color.FromArgb(colors.WinBg);
        page.BackgroundColor = winBg;
        if (page.Parent is NavigationPage nav)
        {
            nav.BackgroundColor = winBg;
            MauiTheme.ApplyColors(nav.Resources, colors);
        }

        ForceOpaqueSurfaces(page, colors);
        RefreshButtonChrome(page);
        MauiRadioTheme.RefreshAll(page);

        // 视觉树未就绪时再刷一次，避免首帧仍吃到 Application 半透明 CardBg
        _ = page.Dispatcher.DispatchAsync(async () =>
        {
            await Task.Yield();
            ForceOpaqueSurfaces(page, colors);
            if (page.Parent is NavigationPage nav2)
                nav2.BackgroundColor = winBg;
            RefreshButtonChrome(page);
            MauiRadioTheme.RefreshAll(page);
            await Task.Delay(50);
            ForceOpaqueSurfaces(page, colors);
        });
    }

    /// <summary>窄屏抽屉菜单：自定义壁纸主题下用实心侧栏，避免透出主内容。</summary>
    public static void ApplyDrawerSurface(View drawer, bool opaque)
    {
        if (!opaque || !ThemePalette.SupportsWallpaper(Current))
        {
            drawer.ClearValue(VisualElement.BackgroundColorProperty);
            return;
        }

        var pane = Color.FromArgb(ThemePalette.ForDialog(Current, maui: true).PaneBg);
        drawer.BackgroundColor = pane;
    }

    private static void ForceOpaqueSurfaces(Element root, ThemeColors colors)
    {
        var winBg = Color.FromArgb(colors.WinBg);
        var cardBg = Color.FromArgb(colors.CardBg);
        var subtleBg = Color.FromArgb(colors.SubtleBg);
        var paneBg = Color.FromArgb(colors.PaneBg);
        var listBg = Color.FromArgb(colors.ListBg);

        void Walk(Element el)
        {
            try
            {
                switch (el)
                {
                    case Border b:
                    {
                        if (TryGetColor(() => b.BackgroundColor) is { } bg && IsTranslucent(bg))
                            b.BackgroundColor = PickSolid(bg, cardBg, subtleBg);
                        break;
                    }
                    case BoxView box:
                    {
                        // 保留遮罩（深色半透明）；浅色半透明托底改实心
                        if (TryGetColor(() => box.Color) is { } bg && IsTranslucent(bg) && !IsDimScrim(bg))
                            box.Color = PickSolid(bg, subtleBg, cardBg);
                        break;
                    }
                    case ScrollView sv:
                    {
                        if (TryGetColor(() => sv.BackgroundColor) is { } bg && IsTranslucent(bg))
                            sv.BackgroundColor = listBg;
                        break;
                    }
                    case CollectionView:
                        // 列表本身保持透明，由外层 Border 托底
                        break;
                    case Grid g:
                    {
                        // 深色半透明 = 弹层遮罩，保留；浅色半透明才改实心
                        if (TryGetColor(() => g.BackgroundColor) is { } bg && IsTranslucent(bg) && !IsDimScrim(bg))
                            g.BackgroundColor = PickSolid(bg, paneBg, winBg);
                        break;
                    }
                    case ContentView content:
                    {
                        // MauiDialogShell 必须保持透明，遮罩由内部 BoxView 绘制
                        if (content is MauiDialogShell)
                            break;
                        if (TryGetColor(() => content.BackgroundColor) is { } bg && IsTranslucent(bg) && !IsDimScrim(bg))
                            content.BackgroundColor = winBg;
                        break;
                    }
                    // 不改 Page.BackgroundColor：遮罩弹层页保持 Transparent
                }
            }
            catch
            {
                // 个别平台控件取色可能抛异常，跳过该节点
            }

            if (el is not IVisualTreeElement tree) return;
            IReadOnlyList<IVisualTreeElement> children;
            try { children = tree.GetVisualChildren(); }
            catch { return; }

            foreach (var child in children)
            {
                if (child is Element e)
                    Walk(e);
            }
        }

        Walk(root);
    }

    private static Color? TryGetColor(Func<Color> read)
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>半透明（有颜色但未实心）。null / 全透明 / 实心均返回 false。</summary>
    private static bool IsTranslucent(Color? c)
    {
        if (c is not { } color) return false;
        var a = color.Alpha;
        return a > 0.02f && a < 0.98f;
    }

    private static bool IsDimScrim(Color? c)
    {
        if (c is not { } color) return false;
        var a = color.Alpha;
        return a > 0.02f && a < 0.98f
               && color.Red < 0.25f && color.Green < 0.25f && color.Blue < 0.25f;
    }

    private static Color PickSolid(Color current, Color primary, Color secondary)
    {
        // 偏灰的半透明更像 SubtleBg；偏白更像 CardBg
        var lum = 0.2126f * current.Red + 0.7152f * current.Green + 0.0722f * current.Blue;
        return lum < 0.92f ? secondary : primary;
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
