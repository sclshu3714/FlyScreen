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
        if (mode == LinuxRunMode.Gui
            && LinuxRuntimeEnvironment.TryEnsureGraphicalEnvironment(out var ensureHint)
            && !string.IsNullOrEmpty(ensureHint))
            Console.WriteLine(ensureHint);

        Console.WriteLine($"玄鸟 Linux | 模式={mode} | {LinuxRuntimeEnvironment.Describe()}");
        if (mode == LinuxRunMode.Gui && LinuxRuntimeEnvironment.IsRunningAsRoot)
            Console.WriteLine("提示: 正以 root 运行，GUI 易因缺少 DISPLAY/Xauthority 失败；建议普通用户 ./FlyScreen --gui");

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

        // 自动：有图形会话 → GUI；否则尝试 WSLg 补 DISPLAY 再判断
        if (LinuxRuntimeEnvironment.HasGraphicalSession)
            return LinuxRunMode.Gui;
        if (LinuxRuntimeEnvironment.TryEnsureGraphicalEnvironment(out _))
            return LinuxRunMode.Gui;

        return LinuxRunMode.Cli;
    }

    private static int RunGui(string[] args)
    {
        try
        {
            _ = LinuxRuntimeEnvironment.TryEnsureGraphicalEnvironment(out _);
            return GuiEntry.Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GUI 启动失败，回退 CLI：{ex.Message}");
            Console.Error.WriteLine(LinuxRuntimeEnvironment.GuiFailureAdvice(ex.Message));
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
              WSL2+WSLg 示例:  export DISPLAY=:0 && ./FlyScreen --gui
              勿用裸 sudo 开 GUI（会丢掉 DISPLAY）；需要时用 sudo -E 或 DISPLAY=:0 sudo -E …
            """);
    }
}
