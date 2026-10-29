using FlyScreen.AppMaui.Services;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Win32Clipboard;
#if WINDOWS
using Windows.Storage;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinDataFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;
#endif

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// MAUI-Windows：虚拟文件剪贴板（与 WPF 一致），粘贴时才拉取。
/// </summary>
public sealed class MauiTextClipboardMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly MauiClipboardApplySink _sink;
    private CancellationTokenSource? _cts;
    private string? _lastInlineKey;
    private string? _lastFilesKey;
    private uint _lastClipboardSeq;
    private long _armedFileSequence;
    private int _materialized;
    private int _armingInline;
    private int _armingFiles;
    private int _busy;

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
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _lastClipboardSeq = Win32FileClipboard.GetSequenceNumber();
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
            ArmVirtualPaste();
    }

    /// <summary>虚拟剪贴板自行处理粘贴，无需拦截热键。</summary>
    public bool TryHandlePasteHotkey() => false;

    private void ArmVirtualPaste()
    {
        if (Interlocked.CompareExchange(ref _armingFiles, 1, 0) != 0) return;
        try
        {
            var seq = _transfers.GetRemotePendingSequence();
            if (seq != 0 && seq == _armedFileSequence && Volatile.Read(ref _materialized) == 0
                && _transfers.OwnsDelayedClipboard)
                return;

            if (!VirtualFilePasteDataObject.TrySetClipboard(
                    new VirtualFilePasteDataObject(MaterializePasteEntriesAsync)))
                return;

            _armedFileSequence = seq;
            Interlocked.Exchange(ref _materialized, 0);
            _transfers.MarkDelayedClipboardOwned(true);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _armingFiles, 0);
        }
    }

    private async Task<IReadOnlyList<ClipboardPasteEntry>?> MaterializePasteEntriesAsync()
    {
        _transfers.SetPasteMergeRoots(null);
        var files = await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(false);
        if (files is { Count: > 0 })
            Interlocked.Exchange(ref _materialized, 1);

        var entries = _transfers.GetLastPasteEntries();
        if (entries is { Count: > 0 })
            return entries;

        if (files is not { Count: > 0 }) return null;
        return files.Select(p =>
        {
            var isDir = Directory.Exists(p);
            long size = 0;
            if (!isDir && File.Exists(p))
            {
                try { size = new FileInfo(p).Length; } catch { /* ignore */ }
            }

            return new ClipboardPasteEntry(Path.GetFileName(p), p, isDir, size);
        }).ToList();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (!_settings.ShareClipboard || _sink.IsSuppressed) continue;

                var seq = Win32FileClipboard.GetSequenceNumber();
                if (seq == 0 || seq == _lastClipboardSeq) continue;
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue;

                try
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    if (_sink.IsSuppressed) continue;

                    if (_settings.TransferFile)
                    {
                        var files = await TryGetLocalFilePathsAsync().ConfigureAwait(false);
                        if (files is { Count: > 0 })
                        {
                            var real = files.Where(p => !_transfers.IsInboxPath(p)).ToList();
                            if (real.Count > 0)
                            {
                                var key = string.Join("|", real);
                                if (key != _lastFilesKey || seq != _lastClipboardSeq)
                                {
                                    if (await _transfers.AnnounceLocalFilesAsync(real).ConfigureAwait(false))
                                    {
                                        _lastFilesKey = key;
                                        _lastClipboardSeq = seq;
                                    }
                                }
                                else
                                {
                                    _lastClipboardSeq = seq;
                                }

                                continue;
                            }
                        }
                    }

                    ClipboardInlineData? inline;
#if WINDOWS
                    inline = await MainThread.InvokeOnMainThreadAsync(MauiWinClipboardInline.TryCaptureAsync)
                        .ConfigureAwait(false);
#else
                    var textOnly = await _sink.TryGetTextAsync().ConfigureAwait(false);
                    inline = string.IsNullOrEmpty(textOnly)
                        ? null
                        : new ClipboardInlineData { Text = textOnly };
#endif
                    if (inline == null || inline.IsEmpty)
                    {
                        _lastClipboardSeq = seq;
                        continue;
                    }

                    if (_sink.IsSameAsLastApplied(inline))
                    {
                        _lastClipboardSeq = seq;
                        continue;
                    }

                    var inlineKey = inline.BuildKey();
                    if (inlineKey == _lastInlineKey && seq == _lastClipboardSeq)
                    {
                        _lastClipboardSeq = seq;
                        continue;
                    }

                    await _transfers.AnnounceLocalInlineAsync(inline).ConfigureAwait(false);
                    _lastInlineKey = inlineKey;
                    _lastClipboardSeq = seq;
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }
        }
    }

    private static async Task<IReadOnlyList<string>?> TryGetLocalFilePathsAsync()
    {
#if WINDOWS
        var win32 = Win32FileClipboard.TryReadPaths();
        if (win32 is { Count: > 0 }) return win32;

        try
        {
            var content = WinClipboard.GetContent();
            if (content == null || !content.Contains(WinDataFormats.StorageItems))
                return null;

            var items = await content.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
            {
                if (item is StorageFile file && !string.IsNullOrWhiteSpace(file.Path))
                    paths.Add(file.Path);
                else if (item is StorageFolder folder && !string.IsNullOrWhiteSpace(folder.Path))
                    paths.Add(folder.Path);
            }

            return paths.Count > 0 ? paths : null;
        }
        catch
        {
            return null;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    private async Task ArmInlineAsync()
    {
        if (Interlocked.CompareExchange(ref _armingInline, 1, 0) != 0) return;
        try
        {
            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(false);
            if (data == null || data.IsEmpty) return;
            MainThread.BeginInvokeOnMainThread(() => _sink.ApplyInline(data));
        }
        catch { /* ignore */ }
        finally { Interlocked.Exchange(ref _armingInline, 0); }
    }
}
