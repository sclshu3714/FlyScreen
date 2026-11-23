using System.IO;
using Avalonia.Threading;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// Mac/Linux 桌面：轮询本机文本/文件并宣告；对端内联/文件宣告后拉取并写入系统剪贴板
///（Files + 可选 PNG），对齐 Win「可 Ctrl+V」体验（本端无延迟虚拟 HDROP）。
/// </summary>
public sealed class DesktopClipboardCoordinator : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private readonly AvaloniaClipboardApplySink _sink;
    private readonly DispatcherTimer _timer;
    private string? _lastLocalInlineKey;
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
            // 纯图片文件已在 ArmRemoteFiles 路径处理；此处避免抢写
            if (_settings.TransferFile
                && _transfers.HasRemotePendingFiles
                && _transfers.RemotePendingFilesAreImagesOnly)
            {
                await ArmRemoteFilesAsync().ConfigureAwait(true);
                return;
            }

            var seq = _transfers.GetRemotePendingInlineSequence();
            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(true);
            if (data == null || data.IsEmpty) return;

            var ready = _transfers.GetLastPasteReadyPaths();
            if (data.Png is { Length: > 0 }
                && ready is { Count: > 0 }
                && ClipboardImagePaths.AreAllImageFilePaths(ready))
            {
                if (await _sink.ApplyFilesAsync(ready, data.Png).ConfigureAwait(true))
                {
                    if (seq != 0)
                        _transfers.MarkRemoteInlineConsumed(seq);
                    return;
                }
            }

            _sink.ApplyInline(data);
            if (seq != 0)
                _transfers.MarkRemoteInlineConsumed(seq);
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
            await ArmRemoteFilesAsync().ConfigureAwait(true);
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

    /// <summary>对端文件宣告：拉取后挂 Files（图文件另附 PNG），使 Ctrl+V 可用。</summary>
    private async Task ArmRemoteFilesAsync()
    {
        if (!_transfers.HasRemotePendingFiles) return;

        var inlineSeq = _transfers.GetRemotePendingInlineSequence();
        ClipboardInlineData? inline = null;
        if (_transfers.HasRemotePendingInline
            && _transfers.RemotePendingFilesAreImagesOnly)
            inline = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(true);

        var files = await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(true);
        if (files is not { Count: > 0 }) return;

        var png = inline?.Png;
        if (png is not { Length: > 0 }
            && ClipboardImagePaths.AreAllImageFilePaths(files))
        {
            foreach (var path in files)
            {
                png = TryEncodePngFromFile(path);
                if (png is { Length: > 0 }) break;
            }
        }

        if (await _sink.ApplyFilesAsync(files, png).ConfigureAwait(true))
        {
            _lastLocalFilesKey = string.Join("|", files);
            if (png is { Length: > 0 })
                _lastLocalInlineKey = ClipboardInlineData.FromPngOnly(png).BuildKey();
            if (inlineSeq != 0)
                _transfers.MarkRemoteInlineConsumed(inlineSeq);
            return;
        }

        // Files 挂载失败时：至少把图写入内联
        if (png is { Length: > 0 })
        {
            _sink.ApplyInline(ClipboardInlineData.FromPngOnly(png));
            if (inlineSeq != 0)
                _transfers.MarkRemoteInlineConsumed(inlineSeq);
        }
    }

    private async Task PollLocalAsync()
    {
        if (!_settings.ShareClipboard) return;
        if (_sink.IsSuppressed) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            IReadOnlyList<string>? files = null;
            if (_settings.TransferFile)
                files = await _sink.TryGetFilePathsAsync().ConfigureAwait(true);

            if (files is { Count: > 0 })
            {
                var key = string.Join("|", files);
                if (key != _lastLocalFilesKey)
                {
                    if (await _transfers.AnnounceLocalFilesAsync(files).ConfigureAwait(true))
                        _lastLocalFilesKey = key;
                }
            }

            var inline = await _sink.TryCaptureInlineAsync().ConfigureAwait(true);
            // 仅有文件路径的图片（预览「复制图片」）→ 编码 PNG 作内联伴侣，便于对端粘到文档
            if ((inline?.Png is not { Length: > 0 })
                && files is { Count: > 0 }
                && ClipboardImagePaths.AreAllImageFilePaths(files))
            {
                foreach (var path in files)
                {
                    var png = TryEncodePngFromFile(path);
                    if (png is not { Length: > 0 }) continue;
                    inline = new ClipboardInlineData
                    {
                        Text = inline?.Text,
                        Html = inline?.Html,
                        Rtf = inline?.Rtf,
                        Csv = inline?.Csv,
                        Png = png
                    };
                    break;
                }
            }

            if (inline == null || inline.IsEmpty) return;
            if (_sink.IsSameAsLastApplied(inline)) return;

            var inlineKey = inline.BuildKey();
            if (inlineKey == _lastLocalInlineKey) return;
            await _transfers.AnnounceLocalInlineAsync(inline).ConfigureAwait(true);
            _lastLocalInlineKey = inlineKey;
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

    private static byte[]? TryEncodePngFromFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (!ClipboardImagePaths.IsImageFileName(path)) return null;

            // 已是 PNG：直接读字节，避免重编码
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 8
                    && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                    return bytes;
            }

            // Avalonia Bitmap.Save → PNG（Mac/Linux/Win 桌面均可）
            using var bmp = new Avalonia.Media.Imaging.Bitmap(path);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
