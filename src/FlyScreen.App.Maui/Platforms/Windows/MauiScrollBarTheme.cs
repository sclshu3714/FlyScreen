#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinSolidBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>覆盖 WinUI 滚动条主题资源，避免系统粗箭头条。</summary>
internal static class MauiScrollBarTheme
{
    public static void Apply(bool dark)
    {
        try
        {
            var app = Microsoft.UI.Xaml.Application.Current;
            if (app?.Resources == null) return;

            var thumb = dark ? Rgb(0x64, 0x74, 0x8B) : Rgb(0xCB, 0xD5, 0xE1);
            var hover = dark ? Rgb(0x94, 0xA3, 0xB8) : Rgb(0x94, 0xA3, 0xB8);
            var drag = dark ? Rgb(0xCB, 0xD5, 0xE1) : Rgb(0x64, 0x74, 0x8B);
            var track = Rgb(0, 0, 0, 0);

            SetBrush(app.Resources, "ScrollBarThumbFill", thumb);
            SetBrush(app.Resources, "ScrollBarThumbFillPointerOver", hover);
            SetBrush(app.Resources, "ScrollBarThumbFillPressed", drag);
            SetBrush(app.Resources, "ScrollBarThumbFillDisabled", Rgb(0x64, 0x74, 0x8B, 0x66));
            SetBrush(app.Resources, "ScrollBarTrackFill", track);
            SetBrush(app.Resources, "ScrollBarTrackFillPointerOver", track);
            SetBrush(app.Resources, "ScrollBarBackground", track);
            SetBrush(app.Resources, "ScrollBarBackgroundPointerOver", track);
        }
        catch
        {
            // ignore
        }
    }

    private static void SetBrush(Microsoft.UI.Xaml.ResourceDictionary rd, string key, global::Windows.UI.Color color)
    {
        if (rd.TryGetValue(key, out var existing) && existing is WinSolidBrush brush)
        {
            brush.Color = color;
            rd[key] = brush;
            return;
        }

        rd[key] = new WinSolidBrush(color);
    }

    private static global::Windows.UI.Color Rgb(byte r, byte g, byte b, byte a = 255)
        => global::Windows.UI.Color.FromArgb(a, r, g, b);
}
#endif
