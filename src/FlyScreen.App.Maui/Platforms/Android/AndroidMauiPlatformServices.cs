#if ANDROID
using FlyScreen.AppMaui.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Models;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android：无桌面钩子；文本剪贴板监视 + Caps=AndroidMobile。</summary>
public sealed class AndroidMauiPlatformServices : IMauiPlatformServices
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _clipSink = new();
    private CancellationTokenSource? _clipCts;
    private string? _lastTextKey;

    public AndroidMauiPlatformServices(AppSettings settings, FileTransferService transfers)
    {
        _settings = settings;
        _transfers = transfers;
    }

    public IClipboardApplySink ClipboardSink => _clipSink;
    public bool SupportsDesktopInput => false;
    public bool SupportsTray => false;
    public bool SupportsAutoStart => false;
    public bool IsFocusOnLocalMachine => true;
    public bool AcceptsRemoteInput => false;

    public event Action<string?>? ControlTargetChanged
    {
        add { }
        remove { }
    }

    public void StartDesktopInput() { }
    public void StopDesktopInput() { }
    public void SetEasyMouse(bool enabled) { }
    public void InjectMouseMoveAbsolute(double nx, double ny) { }
    public void InjectMouseMoveRelative(int dx, int dy) { }
    public void InjectMouseButton(int button, bool down) { }
    public void InjectMouseWheel(int delta, bool horizontal) { }
    public void InjectKey(ushort vk, bool down, bool extended) { }
    public void BeginReceivingRemoteControl(double entryX, double entryY) { }
    public void OnCursorOwnerChanged(string ownerMachine) { }

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
        // 无系统托盘：退到后台，连接继续跑
        var activity = Platform.CurrentActivity;
        activity?.MoveTaskToBack(true);
    }

    public void DisposeTray() { }

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
