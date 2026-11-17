using System.Windows;
using System.Windows.Media;
using FlyScreen.Ui.Theme;

namespace FlyScreen.Ui.Theme.Wpf;

/// <summary>
/// 工具/插件弹窗主题：自定义壁纸主题的半透明表面不能直接拷自主窗，
/// 须用 <see cref="ThemePalette.ForDialog"/> 实心色板。
/// </summary>
public static class WpfDialogTheme
{
    public const string ThemeIdResourceKey = "Theme.Id";

    /// <summary>由宿主 ThemeService.Apply 同步，供插件在无宿主引用时解析当前主题。</summary>
    public static string ActiveThemeId { get; set; } = ThemePalette.LightName;

    public static void ApplyTo(FrameworkElement target, string? themeId = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        themeId ??= ResolveThemeId();
        var map = ThemePalette.ToResourceMap(ThemePalette.ForDialog(themeId));
        var rd = target.Resources;
        foreach (var (key, hex) in map)
            rd[key] = new SolidColorBrush(Parse(hex));

        if (target is Window win)
        {
            win.Background = (Brush)rd["WinBg"];
            win.Foreground = (Brush)rd["Text"];
        }
    }

    public static string ResolveThemeId()
    {
        if (Application.Current?.Resources[ThemeIdResourceKey] is string id
            && !string.IsNullOrWhiteSpace(id))
            return id;
        return string.IsNullOrWhiteSpace(ActiveThemeId) ? ThemePalette.LightName : ActiveThemeId;
    }

    private static Color Parse(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;
}
