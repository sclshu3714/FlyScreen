using Avalonia.Threading;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// Mac/Linux 桌面：轮询本机文本/文件路径并宣告；对端文件宣告时不预拉，
/// 由「接收剪贴板文件」或后续粘贴路径显式拉取（与 Win「粘贴才传」对齐；本端无 CF_HDROP）。
/// </summary>
public sealed class DesktopClipboardCoordinator : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly AvaloniaClipboardApplySink _sink;
    private readonly DispatcherTimer _timer;
    private string? _lastLocalTextKey;
    private string? _lastLocalFilesKey;
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

    private void OnRemoteFilesAnnounced()
    {
        // 与 Win 对齐：宣告时不拉取；由「接收剪贴板文件」显式拉取
    }

    private async Task PollLocalAsync()
    {
        if (!_settings.ShareClipboard) return;
        if (_sink.IsSuppressed) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_settings.TransferFile)
            {
                var files = await _sink.TryGetFilePathsAsync().ConfigureAwait(true);
                if (files is { Count: > 0 })
                {
                    var key = string.Join("|", files);
                    if (key != _lastLocalFilesKey)
                    {
                        if (await _transfers.AnnounceLocalFilesAsync(files).ConfigureAwait(true))
                            _lastLocalFilesKey = key;
                    }

                    return;
                }
            }

            var text = await _sink.TryGetTextAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(text)) return;
            var textKey = $"{text.Length}:{text[..Math.Min(32, text.Length)]}";
            if (textKey == _lastLocalTextKey) return;
            _lastLocalTextKey = textKey;
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
