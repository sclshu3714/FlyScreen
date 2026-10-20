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
    private string? _lastOverlayWarn;
    private long _lastOverlayWarnTicks;

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
        // 触控板页优先：屏蔽电脑键鼠，避免双指针；离开触控板后再允许穿入
        var want = _touchpadWantsSuppress;
        if (_remotePointerSuppressed == want) return;
        _remotePointerSuppressed = want;
        if (want && _receiving)
        {
            EndRemoteCursor();
            ControlTargetChanged?.Invoke(null);
            AppLog.Info("触控板已启用：已断开电脑键鼠控制");
        }
    }

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }

    public void InjectMouseMoveAbsolute(double nx, double ny)
    {
        if (_remotePointerSuppressed || !_receiving) return;

        // 无障碍只更新点击坐标，不画（画交给系统悬浮窗）
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

    private bool _shiftKey;
    private bool _capsKey;

    public void InjectKey(ushort vk, bool down, bool extended)
    {
        if (_remotePointerSuppressed || !_receiving) return;

        // 无障碍已开：完整 IME + 手势路径
        if (FlyScreenInputAccessibilityService.IsEnabled)
        {
            FlyScreenInputAccessibilityService.Instance?.Key(vk, down);
            return;
        }

        // 无障碍未开：编辑器组字条仍可收键（中文依赖系统 IME / 软键盘）
        TrackModifierKeys(vk, down);
        if (RemoteTextInputHub.HasSink
            && RemoteTextInputHub.TryHandleRemoteKey(vk, down, _shiftKey, _capsKey, ctrl: false))
            return;

        PromptTouchInjectionOnce();
    }

    public void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 编辑器 sink 优先：不依赖被控态，避免 TextInput 被 _receiving 门闩丢掉
        if (RemoteTextInputHub.TryAcceptText(text))
            return;

        if (_remotePointerSuppressed || !_receiving) return;

        if (FlyScreenInputAccessibilityService.IsEnabled)
        {
            FlyScreenInputAccessibilityService.Instance?.InsertCommittedText(text);
            return;
        }

        PromptTouchInjectionOnce();
    }

    private void TrackModifierKeys(ushort vk, bool down)
    {
        switch (vk)
        {
            case 0x10 or 0xA0 or 0xA1:
                _shiftKey = down;
                break;
            case 0x14:
                if (down) _capsKey = !_capsKey;
                break;
        }
    }

    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        // 触控板正在控制电脑时，拒绝被电脑反控
        if (_touchpadWantsSuppress)
        {
            AppLog.Info("触控板模式：忽略电脑键鼠穿入");
            return;
        }

        _remotePointerSuppressed = false;
        _receiving = true;

        // 禁止应用内 DecorView 箭头——它只活在玄鸟窗口里，退后台必消失
        VirtualCursorOverlay.Hide();

        if (FlyScreenInputAccessibilityService.IsEnabled)
        {
            // 有悬浮窗权限时不画无障碍箭头，避免双指针乱跳
            FlyScreenInputAccessibilityService.Instance!.SetDrawCursor(!FlyScreenCursorOverlay.CanDraw());
            FlyScreenInputAccessibilityService.Instance!.BeginSession(entryX, entryY);
        }

        if (FlyScreenCursorOverlay.CanDraw())
            FlyScreenCursorOverlay.ShowAt(entryX, entryY);
        else
            PromptOverlayOnce();

        UpdateCursorVisual(entryX, entryY, relative: false, 0, 0);
        // 跨屏打字由电脑组字，收起本机软键盘避免抢焦点
        try
        {
            var activity = Platform.CurrentActivity;
            var token = activity?.CurrentFocus?.WindowToken ?? activity?.Window?.DecorView?.WindowToken;
            if (activity != null && token != null)
            {
                var imm = activity.GetSystemService(global::Android.Content.Context.InputMethodService)
                    as global::Android.Views.InputMethods.InputMethodManager;
                imm?.HideSoftInputFromWindow(token, global::Android.Views.InputMethods.HideSoftInputFlags.None);
            }
        }
        catch { /* ignore */ }

        ControlTargetChanged?.Invoke(null);
        AppLog.Info($"开始接收电脑键鼠 @ ({entryX:0.##},{entryY:0.##}) overlay={FlyScreenCursorOverlay.CanDraw()} showing={FlyScreenCursorOverlay.IsShowing} a11y={FlyScreenInputAccessibilityService.IsEnabled}");
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
    /// 指针只走系统层：悬浮窗 或 无障碍 Overlay。
    /// 绝不走 VirtualCursorOverlay（DecorView），否则退后台/多任务预览里像「画在玄鸟里面」。
    /// </summary>
    private void UpdateCursorVisual(double nx, double ny, bool relative, int dx, int dy)
    {
        // 始终隐藏应用内箭头
        VirtualCursorOverlay.Hide();

        if (FlyScreenCursorOverlay.CanDraw())
        {
            if (!FlyScreenCursorOverlay.IsShowing)
                FlyScreenCursorOverlay.ShowAt(relative ? 0.5 : nx, relative ? 0.5 : ny);

            if (relative)
                FlyScreenCursorOverlay.MoveRelative(dx, dy);
            else
                FlyScreenCursorOverlay.MoveAbsolute(nx, ny);

            if (FlyScreenCursorOverlay.IsShowing)
            {
                FlyScreenInputAccessibilityService.Instance?.SetDrawCursor(false);
                return;
            }

            // 悬浮窗尚未加上：短暂用无障碍层顶上（仍可盖其他应用）
            var err = FlyScreenCursorOverlay.LastError;
            if (!string.IsNullOrEmpty(err))
            {
                var now = Environment.TickCount64;
                if (!string.Equals(err, _lastOverlayWarn, StringComparison.Ordinal) || now - _lastOverlayWarnTicks > 5000)
                {
                    _lastOverlayWarn = err;
                    _lastOverlayWarnTicks = now;
                    AppLog.Warn($"系统悬浮指针未就绪: {err}");
                }
            }
        }
        else
        {
            PromptOverlayOnce();
        }

        // 唯一备用：无障碍系统 Overlay（不要 VirtualCursorOverlay）
        FlyScreenInputAccessibilityService.Instance?.SetDrawCursor(true);
    }

    private void EndRemoteCursor()
    {
        _receiving = false;
        VirtualCursorOverlay.Hide();
        RemoteCompositionOverlay.Hide();
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
