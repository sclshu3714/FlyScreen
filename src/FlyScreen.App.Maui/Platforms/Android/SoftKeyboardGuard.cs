#if ANDROID
using Android.Views.InputMethods;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 跨屏被控时收起本机软键盘，避免与电脑组字候选条抢焦点。
/// 不影响本机主动点「键盘」条等用户操作（仅在被控态强制隐藏）。
/// </summary>
internal static class SoftKeyboardGuard
{
    public static bool SuppressWhileReceiving { get; set; }

    public static void Hide()
    {
        try
        {
            var activity = Platform.CurrentActivity;
            if (activity == null) return;
            var token = activity.CurrentFocus?.WindowToken
                        ?? activity.Window?.DecorView?.WindowToken;
            if (token == null) return;
            var imm = activity.GetSystemService(global::Android.Content.Context.InputMethodService)
                as InputMethodManager;
            imm?.HideSoftInputFromWindow(token, HideSoftInputFlags.None);
        }
        catch
        {
            /* ignore */
        }
    }
}
#endif
