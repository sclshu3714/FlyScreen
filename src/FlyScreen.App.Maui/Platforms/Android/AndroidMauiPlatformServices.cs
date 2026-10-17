#if ANDROID
using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Models;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// Android：文本剪贴板 + Caps=AndroidMobile（含 InjectInput/EdgeMouse）。
/// 无障碍服务已开启时用它的叠层画指针（后台也能显示）。悬浮窗只在服务未开启时兜底。
/// </summary>
public sealed class AndroidMauiPlatformServices : IMauiPlatformServices
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _clipSink = new();
    private CancellationTokenSource? _clipCts;
    private string? _lastTextKey;
    private bool _receiving;
    private bool _cursorViaOverlay;
    private bool _promptedOverlay;
    private bool _promptedTouchInjection;

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
    public bool AcceptsRemoteInput => _receiving;

    public event Action<string?>? ControlTargetChanged;

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }

    public void InjectMouseMoveAbsolute(double nx, double ny)
    {
        if (FlyScreenInputAccessibilityService.IsEnabled)
            FlyScreenInputAccessibilityService.Instance?.MoveAbsolute(nx, ny);
        if (AccessibilityCursorShowing())
            return;
        if (!_cursorViaOverlay)
            _cursorViaOverlay = FlyScreenCursorOverlay.TryBegin(nx, ny);
        if (_cursorViaOverlay)
            FlyScreenCursorOverlay.MoveAbsolute(nx, ny);
        else if (_receiving)
            PromptOverlayOnce();
    }

    public void InjectMouseMoveRelative(int dx, int dy)
    {
        if (!_receiving && !FlyScreenInputAccessibilityService.IsEnabled) return;
        if (FlyScreenInputAccessibilityService.IsEnabled)
            FlyScreenInputAccessibilityService.Instance?.MoveRelative(dx, dy);
        if (AccessibilityCursorShowing())
            return;
        if (_cursorViaOverlay)
            FlyScreenCursorOverlay.MoveRelative(dx, dy);
    }

    public void InjectMouseButton(int button, bool down)
        => InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Click(button, down));

    public void InjectMouseWheel(int delta, bool horizontal)
        => InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Wheel(delta, horizontal));

    public void InjectKey(ushort vk, bool down, bool extended)
        => InjectTouch(() => FlyScreenInputAccessibilityService.Instance?.Key(vk, down));

    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        _receiving = true;
        if (FlyScreenInputAccessibilityService.IsEnabled)
        {
            _cursorViaOverlay = false;
            FlyScreenCursorOverlay.End();
            FlyScreenInputAccessibilityService.Instance!.BeginSession(entryX, entryY);
            var entryXCopy = entryX;
            var entryYCopy = entryY;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(400);
                if (!_receiving || AccessibilityCursorShowing()) return;
                if (FlyScreenCursorOverlay.TryBegin(entryXCopy, entryYCopy))
                    _cursorViaOverlay = true;
                else
                    PromptOverlayOnce();
            });
        }
        else if (FlyScreenCursorOverlay.TryBegin(entryX, entryY))
        {
            _cursorViaOverlay = true;
        }
        else
        {
            _cursorViaOverlay = false;
            PromptOverlayOnce();
        }
        ControlTargetChanged?.Invoke(null);
    }

    public void OnCursorOwnerChanged(string ownerMachine)
    {
        if (string.IsNullOrWhiteSpace(ownerMachine)) return;
        var local = string.Equals(ownerMachine, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ownerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (local) return;
        // 光标归电脑：结束本机被控态
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

    private void EndRemoteCursor()
    {
        _receiving = false;
        _cursorViaOverlay = false;
        FlyScreenCursorOverlay.End();
        FlyScreenInputAccessibilityService.Instance?.EndSession();
    }

    /// <summary>无障碍全屏层已经加上时，关掉悬浮窗指针，避免两套叠在一起。</summary>
    private bool AccessibilityCursorShowing()
    {
        if (FlyScreenInputAccessibilityService.Instance?.IsCursorVisible != true)
            return false;
        if (_cursorViaOverlay)
        {
            _cursorViaOverlay = false;
            FlyScreenCursorOverlay.End();
        }
        return true;
    }

    /// <summary>点击、滑动、按键才会走系统手势接口。鼠标穿入本身不请求这项授权。</summary>
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
                "电脑鼠标移入本机时，只在屏幕上显示指针。请允许「显示在其他应用上层」。这不是无障碍权限，也和穿边模式里的「无障碍」无关。",
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
                "指针移入不需要无障碍权限。在其他应用上点击或滑动时，Android 只提供「玄鸟键鼠注入」这项系统服务，它和设置里的穿边模式「无障碍」不是一回事。",
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
