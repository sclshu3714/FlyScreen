using FlyScreen.Mac.Input;
using FlyScreen.Mac.Permissions;
using FlyScreen.Services.Network;

namespace FlyScreen.Mac.LockInput;

/// <summary>macOS 锁屏输入助手：检测锁屏 + 宣告能力；注入复用 CGEvent。</summary>
public static class MacLockScreenInputHost
{
    private static MacInputInjector? _injector;

    internal static MacInputInjector? Injector => _injector;

    public static bool IsRunning => MacLockScreenInputBridge.HelperEnabled && (_injector?.IsAvailable ?? false);

    public static Func<Task>? ConnectionsBroadcastPeerInfo { get; set; }

    /// <summary>由 MacAppHost 绑定同一注入实例，避免重复创建。</summary>
    public static void BindInjector(MacInputInjector injector) => _injector = injector;

    public static void InitializeFromSettings(bool enabledInSettings)
    {
        MacLockScreenSession.EnsureStarted();
        if (enabledInSettings)
            _ = Apply(true);
        else
        {
            MacLockScreenInputBridge.HelperEnabled = false;
            LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, false);
        }
    }

    /// <summary>开启需辅助功能权限；关闭仅清标志。</summary>
    public static bool Apply(bool wantEnabled)
    {
        MacLockScreenSession.EnsureStarted();
        if (!wantEnabled)
        {
            MacLockScreenInputBridge.HelperEnabled = false;
            LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, false);
            try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
            return true;
        }

        if (!AccessibilityPermission.IsMacOS)
            return false;

        _injector ??= new MacInputInjector();
        if (!_injector.IsAvailable)
        {
            try { AccessibilityPermission.OpenSystemSettings(); } catch { /* ignore */ }
            MacLockScreenInputBridge.HelperEnabled = false;
            LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, false);
            try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
            return false;
        }

        MacLockScreenInputBridge.HelperEnabled = true;
        LocalPeerIdentity.SetFeatureCap(PeerCaps.LockScreenInput, true);
        try { _ = ConnectionsBroadcastPeerInfo?.Invoke(); } catch { /* ignore */ }
        return true;
    }
}
