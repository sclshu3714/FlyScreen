using FlyScreen.Ui.Theme;

namespace FlyScreen.Ui.Theme.Maui;

/// <summary>
/// MAUI 主题入口：注入 Shared 色板与度量令牌。
/// 控件 Style 见同目录 Maui/*.xaml（由 App.Maui 合并；结构与 Wpf 主题对齐）。
/// 用法：Application.Resources.MergedDictionaries.Add(new MauiTheme());
/// </summary>
public sealed class MauiTheme : ResourceDictionary
{
    public MauiTheme()
    {
        ApplyColors(ThemePalette.Light);
        ApplyMetrics();
    }

    /// <summary>按浅/深色覆写同名 Color 资源（供 MauiThemeService 调用）。</summary>
    public static void ApplyColors(ResourceDictionary target, ThemeColors colors)
    {
        foreach (var (key, hex) in ThemePalette.ToResourceMap(colors))
            target[key] = Color.FromArgb(hex);
    }

    private void ApplyColors(ThemeColors colors) => ApplyColors(this, colors);

    private void ApplyMetrics()
    {
        this["Space.Xs"] = ThemeMetrics.SpaceXs;
        this["Space.Sm"] = ThemeMetrics.SpaceSm;
        this["Space.Md"] = ThemeMetrics.SpaceMd;
        this["Space.Lg"] = ThemeMetrics.SpaceLg;
        this["Space.Xl"] = ThemeMetrics.SpaceXl;
        this["Space.Xxl"] = ThemeMetrics.SpaceXxl;
        this["Pad.Card"] = new Thickness(ThemeMetrics.SpaceXl);
        this["Control.Height"] = ThemeMetrics.ControlHeight;
        this["Control.HeightTall"] = ThemeMetrics.ControlHeightTall;
        this["Button.MinWidth"] = ThemeMetrics.ButtonMinWidth;
        this["Font.Body"] = ThemeMetrics.FontSizeBody;
        this["Font.Caption"] = ThemeMetrics.FontSizeCaption;
        this["Font.Title"] = ThemeMetrics.FontSizeTitle;
        this["Font.TitleLarge"] = ThemeMetrics.FontSizeTitleLarge;
        this["Radius.Sm"] = ThemeMetrics.RadiusSm;
        this["Radius.Md"] = ThemeMetrics.RadiusMd;
        this["Radius.Lg"] = ThemeMetrics.RadiusLg;
    }
}
