namespace FlyScreen.Mac.LockInput;

/// <summary>
/// 锁屏且助手开启时优先 CGEvent 注入。
/// 说明：macOS 登录窗口（loginwindow）仍可能拦截合成事件；锁屏壁纸/部分密码框通常可用。
/// </summary>
public static class MacLockScreenInputBridge
{
    public static bool HelperEnabled { get; set; }

    public static bool ShouldRouteToHelper
        => HelperEnabled
           && MacLockScreenSession.IsLocked
           && (MacLockScreenInputHost.Injector?.IsAvailable ?? false);

    public static bool TryMoveAbsoluteNormalized(double nx, double ny)
    {
        if (!ShouldRouteToHelper) return false;
        MacLockScreenInputHost.Injector?.MoveAbsoluteNormalized(nx, ny);
        return true;
    }

    public static bool TryMoveRelative(int dx, int dy)
    {
        if (!ShouldRouteToHelper) return false;
        MacLockScreenInputHost.Injector?.MoveRelative(dx, dy);
        return true;
    }

    public static bool TryMouseButton(int button, bool down)
    {
        if (!ShouldRouteToHelper) return false;
        MacLockScreenInputHost.Injector?.MouseButton(new Services.Network.MouseButtonPayload
        {
            Button = button,
            Down = down
        });
        return true;
    }

    public static bool TryMouseWheel(int delta, bool horizontal)
    {
        if (!ShouldRouteToHelper) return false;
        MacLockScreenInputHost.Injector?.MouseWheel(new Services.Network.MouseWheelPayload
        {
            Delta = delta,
            Horizontal = horizontal
        });
        return true;
    }

    public static bool TryKey(ushort vk, bool down, bool extended)
    {
        _ = extended;
        if (!ShouldRouteToHelper) return false;
        MacLockScreenInputHost.Injector?.Key(new Services.Network.KeyEventPayload
        {
            VirtualKey = vk,
            Down = down,
            Extended = extended
        });
        return true;
    }

    public static bool TryInsertText(string text)
    {
        if (!ShouldRouteToHelper) return false;
        MacLockScreenInputHost.Injector?.Key(new Services.Network.KeyEventPayload
        {
            Text = text,
            Down = true
        });
        return true;
    }
}
