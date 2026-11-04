using FlyScreen.ScreenExtend;
using FlyScreen.WinApi;

namespace FlyScreen.TopologyGuard;

/// <summary>
/// 独立命名监视进程：主应用被任务管理器按组结束时本进程仍存活，
/// 父 PID 消失后执行 DisplaySwitch /internal。
/// 用法：FlyScreen.TopologyGuard.exe &lt;parentPid&gt;
/// 显示名「玄鸟:守护」（AssemblyTitle），exe 名仍为 FlyScreen.TopologyGuard.exe。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        WindowsAppUserModel.EnsureProcessId(WindowsAppUserModel.GuardAppId);

        if (args.Length < 1 || !int.TryParse(args[0], out var parentPid) || parentPid <= 0)
            return 1;

        DisplayTopologyWatchdog.RunForParent(parentPid);
        return 0;
    }
}
