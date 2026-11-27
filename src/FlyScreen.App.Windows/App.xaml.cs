using System.Windows;
using FlyScreen.Helpers;
using FlyScreen.Services;
using FlyScreen.Tools;

namespace FlyScreen;

public partial class App : System.Windows.Application
{
    private SingleInstanceHelper? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 须在首次检查更新前：Win10 用 WinHTTP/WinINet，避免 Sockets DNS「不知道这样的主机」
        FlyScreen.Update.WindowsUpdateHttpBootstrap.Ensure();

        // 任务栏显示名「玄鸟」；拓扑守护嵌入主程序，运行时解压（仍单文件分发）
        FlyScreen.WinApi.WindowsAppUserModel.EnsureProcessId(
            FlyScreen.WinApi.WindowsAppUserModel.MainAppId);

        if (ToolUninstallBatchCli.TryGetAnswersPath(e.Args, out var answersPath))
        {
            // 须在返回 OnStartup 前清掉 StartupUri，否则会弹出主窗导致批处理进程不退出、卸载器卡在 22%
            StartupUri = null;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunToolUninstallBatchAsync(e, answersPath);
            return;
        }

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

        // 安装包布局：补写「应用和功能 → 修改」入口（旧安装可能为灰色）
        FlyScreen.Services.Update.ArpModifyRegistration.EnsureIfInstallerLayout();

        // Core 模型属性通知回投 WPF UI 线程
        UiMarshal.Configure(
            post: a => Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background),
            send: a =>
            {
                if (Dispatcher.CheckAccess()) a();
                else Dispatcher.Invoke(a);
            });

        AppHost.EnsureCreated();

        // 软退出兜底：若 UI 未走 ExitApp，仍尽量收拢扩展拓扑（杀进程无法触发）
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (FlyScreen.ScreenExtend.DisplayTopologyDirty.IsMarked)
                    FlyScreen.ScreenExtend.DisplayProjection.TryApply(
                        FlyScreen.Services.ScreenExtend.DisplayProjectionMode.Internal);
            }
            catch { /* ignore */ }
        };

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

    private async Task RunToolUninstallBatchAsync(StartupEventArgs e, string answersPath)
    {
        StartupUri = null;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!SingleInstanceHelper.TryStartPrimary(out _singleInstance))
        {
            AppLog.Error("批处理卸载失败：主程序仍在运行，请先退出玄鸟");
            // 强制退出：仅 Shutdown 在无窗体时偶发不结束进程，卸载器会一直 WaitForExit
            ForceExit(2);
            return;
        }

        try
        {
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            AppLog.Error("批处理卸载 OnStartup 失败: " + ex);
            ForceExit(1);
            return;
        }

        UiMarshal.Configure(
            post: a => Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background),
            send: a =>
            {
                if (Dispatcher.CheckAccess()) a();
                else Dispatcher.Invoke(a);
            });

        var exit = 1;
        try
        {
            AppHost.EnsureCreated();
            // 勿回 UI 线程：批处理无主窗，ConfigureAwait(true) 可能永不续跑
            exit = await ToolUninstallBatchCli.RunAsync(answersPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("批处理卸载异常: " + ex);
            exit = 1;
        }
        finally
        {
            try { _singleInstance?.Dispose(); } catch { /* ignore */ }
            _singleInstance = null;
        }

        ForceExit(exit);
    }

    private static void ForceExit(int code)
    {
        try
        {
            Current?.Shutdown(code);
        }
        catch { /* ignore */ }

        // 兜底：确保卸载器侧 WaitForExit 能结束
        try { Environment.Exit(code); }
        catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.Dispose(); }
        catch { /* 退出路径忽略 */ }
        _singleInstance = null;
        base.OnExit(e);
    }
}
