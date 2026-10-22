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
        // 触控板「键盘」条：允许系统拼音候选（勿用 NoSuggestions）
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FlyScreenPhoneIme", (handler, view) =>
        {
            if (view is not Entry entry) return;
            if (entry.StyleClass is null || !entry.StyleClass.Contains("PhoneIme")) return;
            if (handler.PlatformView is not Android.Widget.EditText edit) return;
            edit.ShowSoftInputOnFocus = true;
            edit.InputType = Android.Text.InputTypes.ClassText;
            edit.ImeOptions = Android.Views.InputMethods.ImeAction.Done;
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
#endif

        var host = MauiAppHost.EnsureCreated();
#if WINDOWS
        host.AttachPlatform(new Platforms.Windows.WindowsMauiPlatformServices(
            host.Settings, host.Connections, host.Transfers));
#elif ANDROID
        host.AttachPlatform(new Platforms.Android.AndroidMauiPlatformServices(
            host.Settings, host.Transfers));
        // 用户程序 Fly.* 控机：无障碍服务连上后也会再设一次
        ProgramDevice.Current = Platforms.Android.AndroidProgramDeviceControl.Instance;
#elif MACCATALYST || IOS
        // Mac / iOS：壳可跑；键鼠注入等仍走 Null（正式桌面 Mac 见 Avalonia）
        host.AttachPlatform(NullMauiPlatformServices.Instance);
#endif

        builder.Services.AddSingleton(host);
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<PhoneTouchpadPage>();

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
