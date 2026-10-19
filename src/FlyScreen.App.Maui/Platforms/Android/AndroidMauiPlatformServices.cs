#if ANDROID
using FlyScreen.AppMaui.Services;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Models;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// Android 键鼠接收：
/// 1) 悬浮小窗画指针（其他应用上层可见）
/// 2) 未加窗成功前绝不关掉无障碍/应用内箭头（避免「能点但看不见」）
/// 3) 电脑主动穿入时解除触控板屏蔽
/// </summary>
public sealed class AndroidMauiPlatformServices : IMauiPlatformServices
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _clipSink = new();
    private CancellationTokenSource? _clipCts;
    private string? _lastTextKey;
    private bool _receiving;
    private bool _promptedOverlay;
    private bool _promptedTouchInjection;
    private bool _remotePointerSuppressed;
    private bool _touchpadWantsSuppress;

    public AndroidMauiPlatformServices(AppSettings settings, FileTransferService transfers)
    {
        _settings = settings;
        _transfers = transfers;
    }

    public IClipboardApplySink ClipboardSink => _clipSink;
    public bool SupportsDesktopInput => false;
    public bool SupportsTray => false;
    public bool SupportsAutoStart => false;
    public bool IsFocusOnLocalMachine => !_receiving;
    public bool AcceptsRemoteInput => _receiving && !_remotePointerSuppressed;

    public event Action<string?>? ControlTargetChanged;

    public void SetRemotePointerSuppressed(bool suppressed)
    {
        _touchpadWantsSuppress = suppressed;
        ApplySuppressState();
    }

    /// <summary>Activity OnPause：退后台时允许电脑控制本机并显示指针。</summary>
    public void NotifyAppBackgrounded()
    {
        if (!_remotePointerSuppressed) return;
        _remotePointerSuppressed = false;
        AppLog.Info("已退到后台：允许电脑键鼠穿入并显示指针");
    }

    /// <summary>Activity OnResume：若仍在触控板页，恢复屏蔽。</summary>
    public void NotifyAppForegrounded()
    {
        ApplySuppressState();
    }

    private void ApplySuppressState()
    {
        // 正在被电脑控制时，不以触控板屏蔽盖过（否则指针永远起不来）
        var want = _touchpadWantsSuppress && !_receiving;
        if (_remotePointerSuppressed == want) return;
        _remotePointerSuppressed = want;
        if (want && _receiving)
        {
            EndRemoteCursor();
            ControlTargetChanged?.Invoke(null);
        }
    }

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }

    public void InjectMouseMoveAbsolute(double nx, double ny)
    {
        if (_remotePointerSuppressed || !_receiving) return;

        if (FlyScreenInputAccessibilityService.IsEnabled)
            FlyScreenInputAccessibilityService.Instance?.MoveAbsolute(nx, ny);

        UpdateCursorVisual(nx, ny, relative: false, 0, 0);
    }

    public void InjectMouseMoveRelative(int dx, int dy)
    {
        if (_remotePointerSuppressed || !_receiving) return;

        if (FlyScreenInputAccessibilityService.IsEnabled)
            FlyScreenInputAccessibilityService.Instance?.MoveRelative(dx, dy);

        UpdateCursorVisual(0, 0, relative: true, dx, dy);
    }

    public void InjectMouseButton(int button, bool down)
    {
        if (_remotePointerSuppressed || !_receiving) return;
        InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Click(button, down));
    }

    public void InjectMouseWheel(int delta, bool horizontal)
    {
        if (_remotePointerSuppressed || !_receiving) return;
        InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Wheel(delta, horizontal));
    }

    public void InjectKey(ushort vk, bool down, bool extended)
    {
        if (_remotePointerSuppressed || !_receiving) return;
        InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Key(vk, down));
    }

    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        // 电脑主动穿入：强制允许接收（触控板屏蔽让路）
        _remotePointerSuppressed = false;
        _receiving = true;

        if (FlyScreenInputAccessibilityService.IsEnabled)
        {
            // 先开无障碍会话；悬浮窗未确认前保持画箭头
            FlyScreenInputAccessibilityService.Instance!.SetDrawCursor(true);
            FlyScreenInputAccessibilityService.Instance!.BeginSession(entryX, entryY);
        }

        UpdateCursorVisual(entryX, entryY, relative: false, 0, 0);
        ControlTargetChanged?.Invoke(null);
        AppLog.Info($"开始接收电脑键鼠 @ ({entryX:0.##},{entryY:0.##}) overlay={FlyScreenCursorOverlay.CanDraw()} a11y={FlyScreenInputAccessibilityService.IsEnabled}");
    }

    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;
        var local = string.Equals(ownerMachine, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ownerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (local) return;
        if (_receiving)
            EndRemoteCursor();
    }

    public void RestoreCursorWhenLeavingCluster()
    {
        if (!_receiving) return;
        EndRemoteCursor();
        ControlTargetChanged?.Invoke(null);
    }

    public void StartClipboardMonitor()
    {
        _clipCts = new CancellationTokenSource();
        _ = LoopAsync(_clipCts.Token);
    }

    public void StopClipboardMonitor()
    {
        try { _clipCts?.Cancel(); } catch { /* ignore */ }
        _clipCts?.Dispose();
        _clipCts = null;
    }

    public void ArmRemoteClipboardForPaste()
    {
        if (!_settings.ShareClipboard) return;
        if (_transfers.HasRemotePendingInline)
            _ = ArmInlineAsync();
    }

    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }

    public void HideToBackground(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null)
    {
        var activity = Platform.CurrentActivity;
        activity?.MoveTaskToBack(true);
    }

    public void DisposeTray() { }

    /// <summary>
    /// 指针显示策略（关键）：
    /// - 有权限就尝试悬浮小窗；成功后才关掉备用箭头
    /// - 失败则继续用无障碍箭头 + 应用内箭头，绝不能三路全关
    /// </summary>
    private void UpdateCursorVisual(double nx, double ny, bool relative, int dx, int dy)
    {
        if (FlyScreenCursorOverlay.CanDraw())
        {
            if (!FlyScreenCursorOverlay.IsShowing)
            {
                if (relative)
                    FlyScreenCursorOverlay.TryBegin(0.5, 0.5);
                else
                    FlyScreenCursorOverlay.TryBegin(nx, ny);
            }

            if (FlyScreenCursorOverlay.IsShowing)
            {
                if (relative)
                    FlyScreenCursorOverlay.MoveRelative(dx, dy);
                else
                    FlyScreenCursorOverlay.MoveAbsolute(nx, ny);

                // 仅在悬浮窗真正加成功后关掉备用箭头
                FlyScreenInputAccessibilityService.Instance?.SetDrawCursor(false);
                VirtualCursorOverlay.Hide();
                return;
            }
        }
        else
        {
            PromptOverlayOnce();
        }

        // 备用：无障碍箭头（可盖其他应用）+ 应用内箭头（前台兜底）——绝不能全关
        FlyScreenInputAccessibilityService.Instance?.SetDrawCursor(true);
        if (relative)
            VirtualCursorOverlay.MoveByPixels(dx, dy);
        else
            VirtualCursorOverlay.ShowNormalized(nx, ny);

        if (FlyScreenCursorOverlay.CanDraw() && !string.IsNullOrEmpty(FlyScreenCursorOverlay.LastError))
            AppLog.Warn($"悬浮指针未显示，已用备用箭头。原因: {FlyScreenCursorOverlay.LastError}");
    }

    private void EndRemoteCursor()
    {
        _receiving = false;
        VirtualCursorOverlay.Hide();
        FlyScreenCursorOverlay.End();
        FlyScreenInputAccessibilityService.Instance?.EndSession();
        ApplySuppressState();
    }

    private void InjectTouch(Action inject)
    {
        if (FlyScreenInputAccessibilityService.IsEnabled)
        {
            inject();
            return;
        }
        PromptTouchInjectionOnce();
    }

    private void PromptOverlayOnce()
    {
        if (_promptedOverlay) return;
        _promptedOverlay = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
            {
                OpenOverlaySettings();
                return;
            }

            var go = await page.DisplayAlertAsync(
                "需要悬浮窗权限",
                "电脑鼠标控制手机时要看到指针，请允许「显示在其他应用上层」。",
                "去开启",
                "稍后");
            if (go)
                OpenOverlaySettings();
        });
    }

    private void PromptTouchInjectionOnce()
    {
        if (_promptedTouchInjection) return;
        _promptedTouchInjection = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
            {
                FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
                return;
            }

            var go = await page.DisplayAlertAsync(
                "模拟点击需要系统接口",
                "在其他应用上点击/滑动需要开启「玄鸟键鼠注入」无障碍服务。",
                "去开启",
                "稍后");
            if (go)
                FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
        });
    }

    private static void OpenOverlaySettings()
    {
        var ctx = global::Android.App.Application.Context;
        if (ctx == null) return;
        var uri = global::Android.Net.Uri.Parse("package:" + ctx.PackageName);
        var intent = new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionManageOverlayPermission, uri);
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        ctx.StartActivity(intent);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);
                if (!_settings.ShareClipboard || _clipSink.IsSuppressed) continue;
                var text = await _clipSink.TryGetTextAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(text)) continue;
                var key = text.Length + ":" + text.GetHashCode();
                if (key == _lastTextKey) continue;
                _lastTextKey = key;
                await _transfers.AnnounceLocalInlineAsync(new ClipboardInlineData { Text = text })
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }
        }
    }

    private async Task ArmInlineAsync()
    {
        try
        {
            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(false);
            if (data == null || data.IsEmpty) return;
            MainThread.BeginInvokeOnMainThread(() => _clipSink.ApplyInline(data));
        }
        catch { /* ignore */ }
    }
}
#endif
