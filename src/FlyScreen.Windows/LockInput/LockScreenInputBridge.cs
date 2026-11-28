namespace FlyScreen.LockInput;

/// <summary>
/// 注入路由：锁屏或 UAC 等安全桌面且助手可用时走命名管道；否则返回 false 由调用方走本机 SendInput。
/// </summary>
public static class LockScreenInputBridge
{
    /// <summary>设置开启且服务在跑时为 true。</summary>
    public static bool HelperEnabled { get; set; }

    public static bool ShouldRouteToHelper
        => SecureDesktopState.ShouldUseHelper;

    public static bool TryMoveAbsoluteNormalized(double nx, double ny)
        => ShouldRouteToHelper && LockScreenInputClient.TryMoveAbsoluteNormalized(nx, ny);

    public static bool TryMoveRelative(int dx, int dy)
        => ShouldRouteToHelper && LockScreenInputClient.TryMoveRelative(dx, dy);

    public static bool TryMouseButton(int button, bool down)
        => ShouldRouteToHelper && LockScreenInputClient.TryMouseButton(button, down);

    public static bool TryMouseWheel(int delta, bool horizontal)
        => ShouldRouteToHelper && LockScreenInputClient.TryMouseWheel(delta, horizontal);

    public static bool TryKey(ushort vk, bool down, bool extended)
        => ShouldRouteToHelper && LockScreenInputClient.TryKey(vk, down, extended);

    public static bool TryInsertText(string text)
        => ShouldRouteToHelper && LockScreenInputClient.TryInsertText(text);
}
