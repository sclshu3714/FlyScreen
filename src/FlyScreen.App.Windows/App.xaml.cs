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

        // 设置了开机自启则确保注册表项与当前 exe 路径一致（升级后路径可能变化）
        var settings = AppHost.Instance.Settings;
        if (settings.AutoStart)
            AutoStartHelper.SetEnabled(true);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error(args.Exception, "未处理错误");
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
