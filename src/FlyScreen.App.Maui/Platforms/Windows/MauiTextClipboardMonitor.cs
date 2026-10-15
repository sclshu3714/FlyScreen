using FlyScreen.AppMaui.Services;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 文本剪贴板轮询监视（避免依赖 WPF HwndSource）。
/// 文件剪贴板仍走 DirectTransfer / 拉取后写入文本不适用时由 FileTransfer 落 Inbox。
/// </summary>
public sealed class MauiTextClipboardMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _sink;
    private CancellationTokenSource? _cts;
    private string? _lastTextKey;
    private int _arming;

    public MauiTextClipboardMonitor(
        AppSettings settings,
        FileTransferService transfers,
        MauiClipboardApplySink sink)
    {
        _settings = settings;
        _transfers = transfers;
        _sink = sink;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
    }

    public void ArmRemoteClipboardForPaste()
    {
        if (!_settings.ShareClipboard) return;
        if (_transfers.HasRemotePendingInline)
            _ = ArmInlineAsync();
        if (_settings.TransferFile && _transfers.HasRemotePendingFiles)
            _ = ArmFilesAsync();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(900, ct).ConfigureAwait(false);
                if (!_settings.ShareClipboard || _sink.IsSuppressed) continue;

                var text = await _sink.TryGetTextAsync().ConfigureAwait(false);
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
        if (Interlocked.CompareExchange(ref _arming, 1, 0) != 0) return;
        try
        {
            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(false);
            if (data == null || data.IsEmpty) return;
            MainThread.BeginInvokeOnMainThread(() => _sink.ApplyInline(data));
        }
        catch { /* ignore */ }
        finally { Interlocked.Exchange(ref _arming, 0); }
    }

    private async Task ArmFilesAsync()
    {
        try
        {
            var files = await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(false);
            if (files == null || files.Count == 0) return;
            // 无 WPF 文件剪贴板：提示状态；文件已在 Inbox/Paste 目录
            _transfers.MarkDelayedClipboardOwned(false);
        }
        catch { /* ignore */ }
    }
}
