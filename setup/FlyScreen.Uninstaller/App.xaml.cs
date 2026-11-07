using System.Windows;
using FlyScreen.Uninstaller.Services;

namespace FlyScreen.Uninstaller;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        var ctx = UninstallContext.Detect(args);

        var modify = args.Any(a =>
            a is "/modify" or "--modify" or "/MODIFY" or "-modify");

        // 修改模式不需要复制到 Temp（不删自身）；卸载才需要
        if (!modify && UninstallService.TryRelaunchFromTemp(args, ctx.InstallDir))
        {
            Shutdown();
            return;
        }

        if (modify)
        {
            var win = new ModifyWindow(ctx);
            win.Show();
            return;
        }

        var silent = args.Any(a => a is "/S" or "/silent" or "--silent" or "-s");
        var uninstall = new MainWindow(ctx, silent);
        if (silent)
            uninstall.WindowState = WindowState.Minimized;
        uninstall.Show();
    }
}
