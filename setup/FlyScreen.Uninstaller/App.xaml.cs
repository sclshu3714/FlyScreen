using System.Windows;
using FlyScreen.Uninstaller.Services;

namespace FlyScreen.Uninstaller;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        var ctx = UninstallContext.Detect(args);

        // 若在安装目录内运行，先复制到临时目录再启动，以便删除自身
        if (UninstallService.TryRelaunchFromTemp(args, ctx.InstallDir))
        {
            Shutdown();
            return;
        }

        var silent = args.Any(a => a is "/S" or "/silent" or "--silent" or "-s");
        var win = new MainWindow(ctx, silent);
        if (silent)
            win.WindowState = WindowState.Minimized;
        win.Show();
    }
}
