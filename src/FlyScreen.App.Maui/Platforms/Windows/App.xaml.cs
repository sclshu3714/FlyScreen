using Microsoft.UI.Xaml;

namespace FlyScreen.AppMaui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
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
