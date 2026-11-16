using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using FlyScreen.Ui.Theme;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// Avalonia 界面主题：按 <see cref="ThemePalette"/> 已注册主题覆写 WinParity 画刷，并切换 Fluent RequestedThemeVariant。
/// </summary>
public static class DesktopThemeService
{
    public const string Light = ThemePalette.LightName;
    public const string Dark = ThemePalette.DarkName;

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme) => ThemePalette.IsDark(theme);

    public static void Apply(string? theme, Window? window = null)
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
