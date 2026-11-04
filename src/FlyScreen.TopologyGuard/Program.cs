using FlyScreen.ScreenExtend;

namespace FlyScreen.TopologyGuard;

/// <summary>
/// 独立命名监视进程：主应用被任务管理器按组结束时本进程仍存活，
/// 父 PID 消失后执行 DisplaySwitch /internal。
/// 用法：FlyScreen.TopologyGuard.exe &lt;parentPid&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var parentPid) || parentPid <= 0)
            return 1;

        DisplayTopologyWatchdog.RunForParent(parentPid);
        return 0;
    }
}
