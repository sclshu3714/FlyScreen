namespace FlyScreen.ScreenExtend;

/// <summary>
/// 投影主机：远程回控鼠标时不把本机指针画进画面（观看端只留自己的指针）；
/// 本机物理鼠标一动即恢复。ShowCursor 仅在安装了 LL 钩子的 UI 线程上调用。
/// </summary>
public static class ProjectionHostCursor
{
    private static int _remoteDriving;
    private static int _hidden;

    public static bool SuppressCapturedCursor => Volatile.Read(ref _remoteDriving) != 0;

    /// <summary>任意线程：标记随后的注入移动来自远程回控。</summary>
    public static void BeginRemoteDriving()
        => Volatile.Write(ref _remoteDriving, 1);

    /// <summary>LL 钩子线程：注入事件维持隐藏；物理事件恢复显示。</summary>
    public static void OnHookMouse(bool injected)
    {
        if (injected)
        {
            if (Volatile.Read(ref _remoteDriving) != 0)
                HideOnHookThread();
            return;
        }

        if (Volatile.Read(ref _remoteDriving) == 0 && Volatile.Read(ref _hidden) == 0)
            return;
        Volatile.Write(ref _remoteDriving, 0);
        ShowOnHookThread();
    }

    /// <summary>会话结束：须在钩子所在 UI 线程调用。</summary>
    public static void EndSessionOnHookThread()
    {
        Volatile.Write(ref _remoteDriving, 0);
        ShowOnHookThread();
    }

    private static void HideOnHookThread()
    {
        if (Volatile.Read(ref _hidden) != 0) return;
        try
        {
            while (Native.NativeMethods.ShowCursor(false) >= 0) { }
            Volatile.Write(ref _hidden, 1);
        }
        catch { /* ignore */ }
    }

    private static void ShowOnHookThread()
    {
        if (Volatile.Read(ref _hidden) == 0) return;
        try
        {
            while (Native.NativeMethods.ShowCursor(true) < 0) { }
        }
        catch { /* ignore */ }
        Volatile.Write(ref _hidden, 0);
    }
}
