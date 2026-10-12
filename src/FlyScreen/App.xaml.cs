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
            // 已有实例：已发激活信号，本进程直接退出
            Shutdown();
            return;
        }

        base.OnStartup(e);
        AppHost.EnsureCreated();

        // 设置了开机自启则确保注册表项与当前 exe 路径一致（升级后路径可能变化）
        var settings = AppHost.Instance.Settings;
        if (settings.AutoStart)
            AutoStartHelper.SetEnabled(true);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show("未处理错误: " + args.Exception.Message, "飞鼠");
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
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
