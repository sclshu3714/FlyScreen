#if ANDROID
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android ScrollView / 可滚动视图：细圆角滑块。</summary>
internal static class MauiAndroidScrollChrome
{
    public static void Apply(global::Android.Views.View? view)
    {
        if (view == null) return;
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return;

        try
        {
            var dark = Helpers.MauiThemeService.IsDark(Helpers.MauiThemeService.Current);
            var (thumbArgb, _, trackArgb) = Helpers.MauiScrollChrome.ColorsArgb(dark);

            var thumb = new GradientDrawable();
            thumb.SetColor(thumbArgb);
            thumb.SetCornerRadius(Dp(4));

            var track = new GradientDrawable();
            track.SetColor(trackArgb);

            view.VerticalScrollBarEnabled = true;
            view.ScrollBarStyle = ScrollbarStyles.OutsideOverlay;
            view.VerticalScrollbarThumbDrawable = thumb;
            view.VerticalScrollbarTrackDrawable = track;
            view.HorizontalScrollbarThumbDrawable = thumb;
            view.HorizontalScrollbarTrackDrawable = track;
        }
        catch
        {
            // ignore
        }
    }

    private static float Dp(float value)
    {
        var metrics = global::Android.App.Application.Context?.Resources?.DisplayMetrics;
        return value * (metrics?.Density ?? 2f);
    }
}
#endif
