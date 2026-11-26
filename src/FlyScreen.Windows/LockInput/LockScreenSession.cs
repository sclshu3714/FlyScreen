using Microsoft.Win32;

namespace FlyScreen.LockInput;

/// <summary>本机会话锁屏状态（SessionSwitch）。</summary>
public static class LockScreenSession
{
    private static int _started;
    private static volatile bool _locked;

    public static bool IsLocked => _locked;

    public static event Action<bool>? Changed;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
        }
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var locked = _locked;
        if (e.Reason == SessionSwitchReason.SessionLock)
            locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock
                 or SessionSwitchReason.ConsoleConnect
                 or SessionSwitchReason.SessionLogon)
            locked = false;
        else
            return;

        if (_locked == locked) return;
        _locked = locked;
        try { Changed?.Invoke(locked); } catch { /* ignore */ }
        if (!locked)
            LockScreenInputClient.Reset();
    }
}
