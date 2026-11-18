using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using FlyScreen.Services;
using FlyScreen.Ui.Theme;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// Avalonia 界面主题：按 <see cref="ThemePalette"/> 已注册主题覆写 WinParity 画刷，并切换 Fluent RequestedThemeVariant。
/// </summary>
public static class DesktopThemeService
{
    public const string Light = ThemePalette.LightName;
    public const string Dark = ThemePalette.DarkName;
    public const string Custom = ThemePalette.CustomName;

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme) => ThemePalette.IsDark(theme);

    public static void Apply(string? theme, Window? window = null, string? wallpaperPath = null)
    {
        var info = ThemePalette.Resolve(theme);
        Current = info.Id;
        var dark = info.IsDark;

        var app = Application.Current;
        if (app != null)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        var map = ThemePalette.ToResourceMap(info.ResolveColors());
        foreach (var (key, hex) in map)
            Set(key, hex);
        // Fluent ComboBox 下拉用独立键；自定义壁纸主题须跟实心 PopupBg
        if (map.TryGetValue("PopupBg", out var popupBg))
            Set("ComboBoxDropDownBackground", popupBg);

        window ??= TryGetMainWindow();
        if (window == null) return;

        window.Foreground = Brush("Text") ?? window.Foreground;
        // 主窗 + 壁纸：衬底透明以透壁纸；子对话框请用 ApplyToChildWindow（实心）
        if (window.Background is not SolidColorBrush { Color.A: 0 })
            window.Background = info.SupportsWallpaper
                ? Brushes.Transparent
                : Brush("WinBg") ?? window.Background;

        ApplyWallpaper(window, info.SupportsWallpaper, wallpaperPath);
        window.InvalidateVisual();
    }

    /// <summary>
    /// 子对话框：自定义主题半透明色板只适用于主窗透壁纸；弹窗用实心表面，避免透出主界面。
    /// </summary>
    public static void ApplyToChildWindow(Window window)
    {
        var info = ThemePalette.Resolve(Current);
        var colors = ThemePalette.ForDialog(Current);
        var winBg = new SolidColorBrush(Color.Parse(colors.WinBg));
        var cardBg = new SolidColorBrush(Color.Parse(colors.CardBg));
        var map = ThemePalette.ToResourceMap(colors);
        foreach (var (key, hex) in map)
            window.Resources[key] = new SolidColorBrush(Color.Parse(hex));
        if (map.TryGetValue("PopupBg", out var popupBg))
            window.Resources["ComboBoxDropDownBackground"] = new SolidColorBrush(Color.Parse(popupBg));

        // Mac/Linux：禁止透明层级，否则 DynamicResource 半透明会透出主窗
        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        window.Opacity = 1;
        window.Background = winBg;
        window.Foreground = new SolidColorBrush(Color.Parse(colors.Text));
        if (window.FindControl<Border>("WindowFrame") is { } frame)
            frame.Background = winBg;
        if (window.FindControl<Border>("DialogCard") is { } card)
            card.Background = cardBg;
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = info.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        window.InvalidateVisual();
    }

    private static void ApplyWallpaper(Window window, bool supportsWallpaper, string? wallpaperPath)
    {
        var image = window.FindControl<Image>("WallpaperImage");
        var scrim = window.FindControl<Border>("WallpaperScrim");
        var clip = window.FindControl<Border>("WindowClip");
        var title = window.FindControl<Border>("ClientTitleBar");
        var winBg = Brush("WinBg");
        var paneBg = Brush("PaneBg");
        var path = supportsWallpaper ? UiWallpaperStore.ResolveExisting(wallpaperPath) : null;
        var underlay = new SolidColorBrush(Color.Parse("#F7F8FA"));

        if (image == null && scrim == null) return;

        if (path != null)
        {
            try
            {
                var bmp = new Bitmap(path);
                if (image != null)
                {
                    image.Source = bmp;
                    image.IsVisible = true;
                }
                if (scrim != null) scrim.IsVisible = true;
                if (clip != null) clip.Background = Brushes.Transparent;
                if (title != null) title.Background = paneBg;
            }
            catch
            {
                ClearWallpaperVisual(image, scrim, clip, title, winBg);
            }
        }
        else
        {
            ClearWallpaperVisual(image, scrim, clip, title, winBg);
            if (supportsWallpaper)
            {
                // 无图：浅色实心衬底 + 半透明色板
                if (clip != null) clip.Background = underlay;
                if (title != null) title.Background = paneBg ?? underlay;
            }
        }
    }

    private static void ClearWallpaperVisual(
        Image? image, Border? scrim, Border? clip, Border? title, IBrush? solidWinBg)
    {
        if (image != null)
        {
            image.Source = null;
            image.IsVisible = false;
        }
        if (scrim != null) scrim.IsVisible = false;
        if (clip != null && solidWinBg != null)
            clip.Background = solidWinBg;
        if (title != null && solidWinBg != null)
            title.Background = solidWinBg;
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
