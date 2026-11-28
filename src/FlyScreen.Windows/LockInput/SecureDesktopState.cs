namespace FlyScreen.LockInput;

/// <summary>
/// 缓存「当前是否在安全桌面」（UAC/锁屏 Winlogon）。
/// 通过锁屏助手查询；助手未开时无法可靠探测。
/// </summary>
public static class SecureDesktopState
{
    private static readonly object Gate = new();
    private static long _nextProbeMs;
    private static bool _secure;
    private static string? _name;

    /// <summary>最近一次探测到的输入桌面名。</summary>
    public static string? LastDesktopName
    {
        get { lock (Gate) return _name; }
    }

    /// <summary>是否应走助手抓屏/注入（非 Default，或会话已锁屏）。</summary>
    public static bool ShouldUseHelper
    {
        get
        {
            if (!LockScreenInputBridge.HelperEnabled)
                return false;
            if (LockScreenSession.IsLocked)
                return true;
            return IsNonDefaultInputDesktop;
        }
    }

    public static bool IsNonDefaultInputDesktop
    {
        get
        {
            if (!LockScreenInputBridge.HelperEnabled)
                return false;

            var now = Environment.TickCount64;
            lock (Gate)
            {
                if (now < _nextProbeMs)
                    return _secure;

                _nextProbeMs = now + 300;
                if (LockScreenInputClient.TryQueryDesktop(out var name, out var secure))
                {
                    _name = name;
                    _secure = secure;
                }

                return _secure;
            }
        }
    }

    /// <summary>强制下次重新探测（投影开始/结束时可调用）。</summary>
    public static void Invalidate()
    {
        lock (Gate)
            _nextProbeMs = 0;
    }
}
