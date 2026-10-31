using FlyScreen.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Programs;
using Microsoft.Extensions.Logging;

namespace FlyScreen.AppMaui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if ANDROID
        // 日志/程序/收件箱 → Android/data/com.xuanniao.flyscreen/files（无需所有文件访问）
        Platforms.Android.AndroidPublicDataRoot.TryBootstrap();

        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<PhoneImeEntry, Platforms.Android.PhoneImeEntryHandler>());

        // 触控板「键盘」条：允许系统拼音候选（勿用 NoSuggestions）
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FlyScreenPhoneIme", (handler, view) =>
        {
            if (view is not Entry entry) return;
            if (entry.StyleClass is null || !entry.StyleClass.Contains("PhoneIme")) return;
            if (handler.PlatformView is not Android.Widget.EditText edit) return;
            edit.ShowSoftInputOnFocus = true;
            edit.InputType = Android.Text.InputTypes.ClassText;
            edit.ImeOptions = Platforms.Android.PhoneRemoteEditText.RemoteImeOptions();
            edit.SetSingleLine(true);
            try { edit.PrivateImeOptions = null; } catch { /* ignore */ }
        });

        // RadioButton 首次可见时常吃到 Material 夜间深色底；强制透明背景
        Microsoft.Maui.Handlers.RadioButtonHandler.Mapper.AppendToMapping("FlyScreenRadioChrome", (handler, view) =>
        {
            if (handler.PlatformView is Android.Views.View native)
                native.SetBackgroundColor(Android.Graphics.Color.Transparent);
            if (view is RadioButton rb)
            {
                rb.BackgroundColor = Color.FromArgb("#01000000");
                if (Application.Current?.Resources.TryGetValue("Text", out var tv) == true && tv is Color text)
                    rb.TextColor = text;
            }
        });

        // StyleClass=Mono → 系统等宽；勿用 FontFamily=Consolas（Win 未打包会炸 FontManager）
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FlyScreenMono", (handler, view) =>
        {
            if (view is not Entry { StyleClass: { } sc } || !sc.Contains("Mono")) return;
            if (handler.PlatformView is Android.Widget.EditText edit)
                edit.Typeface = Android.Graphics.Typeface.Monospace;
        });
        Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("FlyScreenMono", (handler, view) =>
        {
            if (view is not Label { StyleClass: { } sc } || !sc.Contains("Mono")) return;
            if (handler.PlatformView is Android.Widget.TextView tv)
                tv.Typeface = Android.Graphics.Typeface.Monospace;
        });
#endif

#if WINDOWS
        // 确保每个 Window 都套上图标（含后续再开的窗口）
        Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping("FlyScreenIcon", (handler, _) =>
        {
            if (handler.VirtualView is Window window)
                Services.MauiWindowIcon.Apply(window);
        });

        // WinUI CheckBox 默认 MinWidth 很大；只清 MinWidth，勿强设 Width（会裁掉勾选图标）
        Microsoft.Maui.Handlers.CheckBoxHandler.Mapper.AppendToMapping("FlyScreenCompact", (handler, _) =>
        {
            if (handler.PlatformView is not Microsoft.UI.Xaml.Controls.CheckBox native) return;
            native.MinWidth = 0;
            native.MinHeight = 0;
            native.Padding = new Microsoft.UI.Xaml.Thickness(0);
            native.Margin = new Microsoft.UI.Xaml.Thickness(0);
            native.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            native.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
        });

        // RadioButton：Win 用 Label 刷字色；Android 须把 Label Content 收成 string（否则显示类型名）
        Microsoft.Maui.Handlers.RadioButtonHandler.Mapper.AppendToMapping("FlyScreenRadioTheme", (handler, view) =>
        {
            if (view is not RadioButton rb) return;
            if (rb.Content is string s)
                Helpers.MauiRadioTheme.SetContent(rb, s);
            else if (rb.Content is Label lbl)
                Helpers.MauiRadioTheme.SetContent(rb, lbl.Text ?? "");
            else
                Helpers.MauiRadioTheme.RefreshAll(rb);
        });

        // 侧栏搜索：去掉 WinUI Entry 默认白底边框，避免「灰底套白框」
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FlyScreenSideSearch", (handler, view) =>
        {
            if (handler.PlatformView is not Microsoft.UI.Xaml.Controls.TextBox native) return;
            if (view is not Entry entry) return;
            if (entry.StyleClass is null || !entry.StyleClass.Contains("SideSearch")) return;
            native.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            native.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            native.Padding = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 4);
        });

        // StyleClass=Mono → WinUI 系统 Consolas；绕过 Maui FontManager 对 Assets/Fonts 相对 URI 的探测
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FlyScreenMono", (handler, view) =>
        {
            if (view is not Entry { StyleClass: { } sc } || !sc.Contains("Mono")) return;
            if (handler.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
                tb.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
        });
        Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("FlyScreenMono", (handler, view) =>
        {
            if (view is not Label { StyleClass: { } sc } || !sc.Contains("Mono")) return;
            if (handler.PlatformView is Microsoft.UI.Xaml.Controls.TextBlock tb)
                tb.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
        });
#endif

        var host = MauiAppHost.EnsureCreated();
#if WINDOWS
        host.AttachPlatform(new Platforms.Windows.WindowsMauiPlatformServices(
            host.Settings, host.Connections, host.Transfers, host.ScreenExtend));
#elif ANDROID
        host.AttachPlatform(new Platforms.Android.AndroidMauiPlatformServices(
            host.Settings, host.Transfers, host.ScreenExtend));
        // 用户程序 Fly.* 控机：无障碍服务连上后也会再设一次
        ProgramDevice.Current = Platforms.Android.AndroidProgramDeviceControl.Instance;
        Platforms.Android.FlyScreenDebugLogHud.Register();
#elif MACCATALYST || IOS
        // Mac / iOS：壳可跑；键鼠注入等仍走 Null（正式桌面 Mac 见 Avalonia）
        host.AttachPlatform(NullMauiPlatformServices.Instance);
#endif

        builder.Services.AddSingleton(host);
        builder.Services.AddSingleton<MainPage>();
        // PhoneTouchpadPage 按目标 peer 每次 new，不注册 Singleton

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        UiMarshal.Configure(
            post: a => MainThread.BeginInvokeOnMainThread(a),
            send: a =>
            {
                if (MainThread.IsMainThread) a();
                else MainThread.InvokeOnMainThreadAsync(a).GetAwaiter().GetResult();
            });

        return app;
    }
}
