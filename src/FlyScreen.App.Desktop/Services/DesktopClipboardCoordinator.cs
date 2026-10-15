using Avalonia.Threading;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// Mac/Linux 桌面：轮询本机文本剪贴板并宣告；对端宣告后拉取并写入本机。
/// 文件延迟粘贴（Win CF_HDROP）不做；文件走 Inbox / DirectTransfer。
/// </summary>
public sealed class DesktopClipboardCoordinator : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly AvaloniaClipboardApplySink _sink;
    private readonly DispatcherTimer _timer;
    private string? _lastLocalTextKey;
    private int _busy;

    public DesktopClipboardCoordinator(
        AppSettings settings,
        FileTransferService transfers,
        AvaloniaClipboardApplySink sink)
    {
        _settings = settings;
        _transfers = transfers;
        _sink = sink;
        _transfers.SetClipboardSink(sink);
        _transfers.RemoteInlineAnnounced += OnRemoteInlineAnnounced;
        _transfers.RemoteFilesAnnounced += OnRemoteFilesAnnounced;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => _ = PollLocalAsync();
        _timer.Start();
    }

    private async void OnRemoteInlineAnnounced()
    {
        if (!_settings.ShareClipboard) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(true);
            if (data != null && !data.IsEmpty)
                _sink.ApplyInline(data);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async void OnRemoteFilesAnnounced()
    {
        if (!_settings.ShareClipboard || !_settings.TransferFile) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            // 非 Win：拉取到 Inbox/PasteStaging，不注册系统文件粘贴
            await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task PollLocalAsync()
    {
        if (!_settings.ShareClipboard) return;
        if (_sink.IsSuppressed) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var text = await _sink.TryGetTextAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(text)) return;
            var key = $"{text.Length}:{text[..Math.Min(32, text.Length)]}";
            if (key == _lastLocalTextKey) return;
            _lastLocalTextKey = key;
            await _transfers.AnnounceLocalInlineAsync(new ClipboardInlineData { Text = text })
                .ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _transfers.RemoteInlineAnnounced -= OnRemoteInlineAnnounced;
        _transfers.RemoteFilesAnnounced -= OnRemoteFilesAnnounced;
    }
}
