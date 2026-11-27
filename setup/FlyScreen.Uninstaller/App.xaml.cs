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

        // 不要在启动时复制到 Temp 并立刻退出：ARP（程序和功能）会认为卸载已结束，
        // 再次点「卸载/更改」就会弹出「请等待当前程序完成卸载或更改」。
        // 删除自身改在卸载完成时 ScheduleSelfDelete / 延迟 rmdir（见 UninstallService）。

        if (modify)
        {
            var win = new ModifyWindow(ctx);
            MainWindow = win;
            win.Show();
            return;
        }

        var silent = args.Any(a => a is "/S" or "/silent" or "--silent" or "-s");
        var uninstall = new MainWindow(ctx, silent);
        MainWindow = uninstall;
        if (silent)
            uninstall.WindowState = WindowState.Minimized;
        uninstall.Show();
    }
}
