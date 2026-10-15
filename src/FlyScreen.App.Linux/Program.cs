using FlyScreen.Helpers;
using FlyScreen.Linux;
using FlyScreen.Linux.Environment;
using FlyScreen.LinuxApp.Cli;
using FlyScreen.LinuxApp.Gui;

namespace FlyScreen.LinuxApp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        UiMarshal.Configure(a => a());

        if (HasFlag(args, "-h", "--help"))
        {
            PrintHelp();
            return 0;
        }

        var mode = ResolveMode(args);
        Console.WriteLine($"玄鸟 Linux | 模式={mode} | {LinuxRuntimeEnvironment.Describe()}");

        return mode switch
        {
            LinuxRunMode.Gui => RunGui(args),
            LinuxRunMode.Daemon => await LinuxCliRunner.RunAsync(args, daemon: true).ConfigureAwait(false),
            _ => await LinuxCliRunner.RunAsync(args, daemon: false).ConfigureAwait(false)
        };
    }

    private static LinuxRunMode ResolveMode(string[] args)
    {
        if (HasFlag(args, "--gui")) return LinuxRunMode.Gui;
        if (HasFlag(args, "--daemon", "-d")) return LinuxRunMode.Daemon;
        if (HasFlag(args, "--cli", "-c")) return LinuxRunMode.Cli;

        // 自动：有图形会话 → GUI；否则 CLI（服务器 / SSH 无转发）
        if (LinuxRuntimeEnvironment.HasGraphicalSession)
            return LinuxRunMode.Gui;

        return LinuxRunMode.Cli;
    }

    private static int RunGui(string[] args)
    {
        try
        {
            return GuiEntry.Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GUI 启动失败，回退 CLI：{ex.Message}");
            return LinuxCliRunner.RunAsync(args, daemon: false).GetAwaiter().GetResult();
        }
    }

    private static bool HasFlag(string[] args, params string[] names)
        => args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));

    private static void PrintHelp()
    {
        Console.WriteLine("""
            玄鸟 (FlyScreen) Linux

            用法:
              FlyScreen                 自动：有 DISPLAY/WAYLAND → GUI，否则 CLI
              FlyScreen --gui           强制图形界面（Avalonia）
              FlyScreen --cli           强制交互命令行
              FlyScreen --daemon        后台服务（无交互，适合 systemd）
              FlyScreen --help

            CLI / Daemon 常用参数:
              --key=<密钥>              设置安全密钥并保存
              --slot=<名>               追加对端机器名到空槽（可多次）
              --start                   启动网络（默认启动）
              --status                  打印一次状态后退出（daemon 忽略）

            环境:
              DISPLAY / WAYLAND_DISPLAY / XDG_SESSION_TYPE 用于会话探测与 Caps。
            """);
    }
}
