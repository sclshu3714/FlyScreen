namespace FlyScreen.Linux.LockInput;

/// <summary>锁屏且助手可用时走 uinput；否则返回 false 由调用方走 X11/Wayland 注入。</summary>
public static class LinuxLockScreenInputBridge
{
    public static bool HelperEnabled { get; set; }

    public static bool ShouldRouteToHelper
        => HelperEnabled && LinuxLockScreenSession.IsLocked && LinuxLockScreenInputHost.InjectorAvailable;

    public static bool TryMoveAbsoluteNormalized(double nx, double ny)
    {
        if (!ShouldRouteToHelper) return false;
        LinuxLockScreenInputHost.Injector?.MoveAbsoluteNormalized(nx, ny);
        return true;
    }

    public static bool TryMoveRelative(int dx, int dy)
    {
        if (!ShouldRouteToHelper) return false;
        LinuxLockScreenInputHost.Injector?.MoveRelative(dx, dy);
        return true;
    }

    public static bool TryMouseButton(int button, bool down)
    {
        if (!ShouldRouteToHelper) return false;
        LinuxLockScreenInputHost.Injector?.MouseButton(button, down);
        return true;
    }

    public static bool TryMouseWheel(int delta, bool horizontal)
    {
        if (!ShouldRouteToHelper) return false;
        LinuxLockScreenInputHost.Injector?.MouseWheel(delta, horizontal);
        return true;
    }

    public static bool TryKey(ushort vk, bool down, bool extended)
    {
        _ = extended;
        if (!ShouldRouteToHelper) return false;
        LinuxLockScreenInputHost.Injector?.Key(vk, down);
        return true;
    }

    public static bool TryInsertText(string text)
    {
        if (!ShouldRouteToHelper) return false;
        LinuxLockScreenInputHost.Injector?.InsertText(text);
        return true;
    }
}
