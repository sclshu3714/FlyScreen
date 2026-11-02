namespace FlyScreen.AppMaui.Helpers;

/// <summary>各端滚动条尽量贴近玄鸟细圆角滑块。</summary>
public static class MauiScrollChrome
{
    public static void ApplyCurrentTheme()
        => Apply(MauiThemeService.IsDark(MauiThemeService.Current));

    public static void Apply(bool dark)
    {
#if WINDOWS
        Platforms.Windows.MauiScrollBarTheme.Apply(dark);
#elif ANDROID
        // Android 滑块色在 Handler 创建原生视图时按当前主题取
        _ = dark;
#endif
    }

    public static (int thumb, int hover, int track) ColorsArgb(bool dark)
    {
        // AARRGGBB
        if (dark)
            return (unchecked((int)0xFF64748B), unchecked((int)0xFF94A3B8), unchecked((int)0x00000000));
        return (unchecked((int)0xFFCBD5E1), unchecked((int)0xFF94A3B8), unchecked((int)0x00000000));
    }
}
