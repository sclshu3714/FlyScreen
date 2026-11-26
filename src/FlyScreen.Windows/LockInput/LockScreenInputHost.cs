using FlyScreen.Services.Network;

namespace FlyScreen.LockInput;

/// <summary>应用启动时同步设置 ↔ 服务 ↔ Caps。</summary>
public static class LockScreenInputHost
{
    public static void InitializeFromSettings(bool enabledInSettings)
    {
        LockScreenSession.EnsureStarted();
        var running = LockScreenInputServiceControl.IsServiceRunning();
        LockScreenInputBridge.HelperEnabled = enabledInSettings && running;
        LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, LockScreenInputBridge.HelperEnabled);
    }

    /// <summary>应用勾选：安装/卸载服务并更新 Caps。返回是否与期望一致。</summary>
    public static bool Apply(bool wantEnabled)
    {
        LockScreenSession.EnsureStarted();
        var ok = LockScreenInputServiceControl.SetEnabled(wantEnabled);
        var running = LockScreenInputServiceControl.IsServiceRunning();
        var effective = wantEnabled && ok && running;
        if (!wantEnabled && !LockScreenInputServiceControl.IsServiceInstalled())
            effective = false;

        // 关闭时以「未安装」为成功；开启以「在跑」为成功
        if (!wantEnabled)
            ok = !running;
        else
            ok = running;

        LockScreenInputBridge.HelperEnabled = ok && wantEnabled;
        LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, LockScreenInputBridge.HelperEnabled);
        try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
        return wantEnabled ? ok : true;
    }

    /// <summary>由宿主注入：能力变化后广播 PeerInfo。</summary>
    public static Func<Task>? ConnectionsBroadcastPeerInfo { get; set; }
}
