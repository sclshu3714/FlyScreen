using System.Diagnostics;
using System.Runtime.InteropServices;
using FlyScreen.Helpers;
using Microsoft.UI.Xaml;

namespace FlyScreen.AppMaui.WinUI;

public partial class App : MauiWinUIApplication
{
    private SingleInstanceHelper? _singleInstance;

    public App()
    {
        // 须在首次检查更新前：Win10 用 WinHTTP/WinINet，避免 Sockets DNS「不知道这样的主机」
        FlyScreen.Update.WindowsUpdateHttpBootstrap.Ensure();

        // 任务栏显示名「玄鸟」：须在任何 UI 之前设置 AppUserModelID（不改 exe 名）
        FlyScreen.WinApi.WindowsAppUserModel.EnsureProcessId(
            FlyScreen.WinApi.WindowsAppUserModel.MainAppId);

        // 与 Win 桌面端共用互斥：同机只允许一个玄鸟；二次启动激活已有界面
        if (!SingleInstanceHelper.TryStartPrimary(out _singleInstance))
        {
            if (Debugger.IsAttached)
            {
                MessageBoxW(0,
                    "玄鸟已在运行（请先退出托盘中的实例，或结束进程后再调试）。",
                    "玄鸟",
                    0);
            }

            Environment.Exit(0);
            return;
        }

        FlyScreen.Services.Update.ArpModifyRegistration.EnsureIfInstallerLayout();

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

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _singleInstance?.Dispose(); }
            catch { /* ignore */ }
            _singleInstance = null;
        };
    }

    protected override MauiApp CreateMauiApp() => global::FlyScreen.AppMaui.MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        _singleInstance?.StartListening(() =>
        {
            try
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(
                    global::FlyScreen.AppMaui.Platforms.Windows.MauiAutostartUi.ActivateMainWindow);
            }
            catch { /* ignore */ }
        });

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
