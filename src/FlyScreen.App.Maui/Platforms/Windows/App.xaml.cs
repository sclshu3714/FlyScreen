using Microsoft.UI.Xaml;

namespace FlyScreen.AppMaui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        // WebView2 勿写入安装目录（Program Files 无写权限会导致 HybridWebView 异常）
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlyScreen", "WebView2");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dir);
        }
        catch { /* ignore */ }

        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => global::FlyScreen.AppMaui.MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        // 启动后再刷一次图标（Handler 映射可能早于标题栏就绪）
        try
        {
            var mauiApp = Microsoft.Maui.Controls.Application.Current;
            if (mauiApp == null) return;
            foreach (var w in mauiApp.Windows)
                global::FlyScreen.AppMaui.Services.MauiWindowIcon.Apply(w);
        }
        catch
        {
            /* ignore */
        }
    }
}
