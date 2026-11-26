using FlyScreen.Services.Network;

namespace FlyScreen.Linux.LockInput;

/// <summary>Linux 锁屏输入助手：打开 uinput 并宣告 LockScreenInput 能力。</summary>
public static class LinuxLockScreenInputHost
{
    private static readonly object Gate = new();
    private static UinputInjector? _injector;

    internal static UinputInjector? Injector
    {
        get { lock (Gate) return _injector; }
    }

    public static bool InjectorAvailable
    {
        get { lock (Gate) return _injector?.IsAvailable == true; }
    }

    public static bool IsRunning => LinuxLockScreenInputBridge.HelperEnabled && InjectorAvailable;

    public static Func<Task>? ConnectionsBroadcastPeerInfo { get; set; }

    public static void InitializeFromSettings(bool enabledInSettings)
    {
        LinuxLockScreenSession.EnsureStarted();
        if (enabledInSettings)
            _ = Apply(true);
        else
        {
            LinuxLockScreenInputBridge.HelperEnabled = false;
            LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, false);
            CloseInjector();
        }
    }

    /// <summary>开启：尝试打开 uinput；关闭：释放设备。</summary>
    public static bool Apply(bool wantEnabled)
    {
        LinuxLockScreenSession.EnsureStarted();
        if (!wantEnabled)
        {
            LinuxLockScreenInputBridge.HelperEnabled = false;
            LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, false);
            CloseInjector();
            try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
            return true;
        }

        lock (Gate)
        {
            _injector ??= new UinputInjector();
            if (!_injector.IsAvailable && !_injector.TryOpen())
            {
                LinuxLockScreenInputBridge.HelperEnabled = false;
                LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, false);
                try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
                return false;
            }
        }

        LinuxLockScreenInputBridge.HelperEnabled = true;
        LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, true);
        try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
        return true;
    }

    private static void CloseInjector()
    {
        lock (Gate)
        {
            try { _injector?.Dispose(); } catch { /* ignore */ }
            _injector = null;
        }
    }
}
