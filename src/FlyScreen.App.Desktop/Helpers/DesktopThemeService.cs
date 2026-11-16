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

        foreach (var (key, hex) in ThemePalette.ToResourceMap(info.ResolveColors()))
            Set(key, hex);

        window ??= TryGetMainWindow();
        if (window == null) return;

        window.Foreground = Brush("Text") ?? window.Foreground;
        // 自绘圆角窗（Background 透明）时由 WindowClip / 壁纸铺色，勿强行盖实心
        if (window.Background is not SolidColorBrush { Color.A: 0 })
            window.Background = info.SupportsWallpaper
                ? Brushes.Transparent
                : Brush("WinBg") ?? window.Background;

        ApplyWallpaper(window, info.SupportsWallpaper, wallpaperPath);
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
