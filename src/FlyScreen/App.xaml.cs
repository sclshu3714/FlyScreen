using System.Windows;
using FlyScreen.Services;

namespace FlyScreen;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppHost.EnsureCreated();
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show("未处理错误: " + args.Exception.Message, "飞鼠");
            args.Handled = true;
        };
    }
}
