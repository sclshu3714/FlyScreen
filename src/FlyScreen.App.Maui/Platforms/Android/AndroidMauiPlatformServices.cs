#if ANDROID
using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Models;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// Android：文本剪贴板 + Caps=AndroidMobile（含 InjectInput/EdgeMouse）。
/// 被桌面控时依赖无障碍服务注入点击/滑动。
/// </summary>
public sealed class AndroidMauiPlatformServices : IMauiPlatformServices
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _clipSink = new();
    private CancellationTokenSource? _clipCts;
    private string? _lastTextKey;
    private bool _receiving;
    private bool _promptedAccessibility;

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
    public bool AcceptsRemoteInput => _receiving && FlyScreenInputAccessibilityService.IsEnabled;

    public event Action<string?>? ControlTargetChanged;

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }

    public void InjectMouseMoveAbsolute(double nx, double ny)
        => FlyScreenInputAccessibilityService.Instance?.MoveAbsolute(nx, ny);

    public void InjectMouseMoveRelative(int dx, int dy)
        => FlyScreenInputAccessibilityService.Instance?.MoveRelative(dx, dy);

    public void InjectMouseButton(int button, bool down)
        => FlyScreenInputAccessibilityService.Instance?.Click(button, down);

    public void InjectMouseWheel(int delta, bool horizontal)
        => FlyScreenInputAccessibilityService.Instance?.Wheel(delta, horizontal);

    public void InjectKey(ushort vk, bool down, bool extended)
        => FlyScreenInputAccessibilityService.Instance?.Key(vk, down);

    public void BeginReceivingRemoteControl(double entryX, double entryY)
    {
        if (!FlyScreenInputAccessibilityService.IsEnabled)
        {
            PromptAccessibilityOnce();
            return;
        }

        _receiving = true;
        FlyScreenInputAccessibilityService.Instance!.BeginSession(entryX, entryY);
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
        {
            _receiving = false;
            FlyScreenInputAccessibilityService.Instance?.EndSession();
        }
    }

    public void RestoreCursorWhenLeavingCluster()
    {
        if (!_receiving) return;
        _receiving = false;
        FlyScreenInputAccessibilityService.Instance?.EndSession();
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

    private void PromptAccessibilityOnce()
    {
        if (_promptedAccessibility) return;
        _promptedAccessibility = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
            {
                FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
                return;
            }

            var go = await page.DisplayAlertAsync(
                "需要无障碍权限",
                "电脑键鼠要控制本机，请开启「玄鸟键鼠注入」无障碍服务。",
                "去开启",
                "稍后");
            if (go)
                FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
        });
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
