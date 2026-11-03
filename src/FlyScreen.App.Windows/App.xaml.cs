using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Services;

namespace FlyScreen;

public partial class App : System.Windows.Application
{
    private SingleInstanceHelper? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstanceHelper.TryStartPrimary(out _singleInstance))
        {
            // 已有实例：已发激活信号；本进程退出（勿走 StartupUri，避免调试器误报启动失败）
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (System.Diagnostics.Debugger.IsAttached)
            {
                System.Windows.MessageBox.Show(
                    "玄鸟已在运行（请先退出托盘中的实例，或结束 FlyScreen.exe 后再调试）。",
                    "玄鸟");
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Core 模型属性通知回投 WPF UI 线程
        UiMarshal.Configure(
            post: a => Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background),
            send: a =>
            {
                if (Dispatcher.CheckAccess()) a();
                else Dispatcher.Invoke(a);
            });

        AppHost.EnsureCreated();

        // 后台同步开机自启，勿阻塞启动（否则会拖住主窗 Loaded / 键鼠钩子，表现为「不能穿边」）
        var autoStart = AppHost.Instance.Settings.AutoStart;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { AutoStartHelper.SetEnabled(autoStart); }
            catch { /* ignore */ }
        });

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("未处理错误: " + args.Exception);
            System.Windows.MessageBox.Show("未处理错误: " + args.Exception.Message, "玄鸟");
            args.Handled = true;
        };

        _singleInstance!.StartListening(() =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow mw)
                    mw.BringToFront();
                else if (Windows.OfType<MainWindow>().FirstOrDefault() is { } existing)
                    existing.BringToFront();
            });
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.Dispose(); }
        catch { /* 退出路径忽略 */ }
        _singleInstance = null;
        base.OnExit(e);
    }
}

