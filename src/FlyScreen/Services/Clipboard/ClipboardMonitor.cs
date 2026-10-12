using System.IO;
using System.Windows;
using System.Windows.Interop;
using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Native;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 监听本机剪贴板：本地复制宣告/同步文本；对端文件用 OLE DataObject，粘贴时再拉取。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private string? _lastText;
    private string? _lastFilesKey;
    private int _busy;
    private int _rendering;

    public ClipboardMonitor(AppSettings settings, FileTransferService transfers)
    {
        _settings = settings;
        _transfers = transfers;
        _transfers.RemoteFilesAnnounced += OnRemoteFilesAnnounced;
    }

    public void Start(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    public void Stop()
    {
        _transfers.RemoteFilesAnnounced -= OnRemoteFilesAnnounced;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source = null;
        ClipboardBridge.ClearPendingDataObject();
    }

    private void OnRemoteFilesAnnounced()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_hwnd == IntPtr.Zero) return;
            if (!_settings.TransferFile || !_settings.ShareClipboard) return;

            var names = _transfers.GetRemotePendingFileNames();
            if (names.Count == 0) return;

            var placeholders = _transfers.CreatePastePlaceholders(names);
            if (placeholders.Count == 0) return;

            // OLE DataObject：右键粘贴可用；后台拉取 + GetData 消息泵，避免卡死
            var ok = ClipboardBridge.OfferPendingFileDrop(
                placeholders,
                async () =>
                {
                    try
                    {
                        var files = await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(false);
                        _transfers.MarkDelayedClipboardOwned(false);
                        return files?.ToArray();
                    }
                    catch
                    {
                        return null;
                    }
                });

            if (!ok)
                ok = ClipboardBridge.OfferDelayedFileDrop(_hwnd);

            if (ok)
                _transfers.MarkDelayedClipboardOwned(true);
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                _ = OnClipboardChangedAsync();
                break;

            case NativeMethods.WM_DESTROYCLIPBOARD:
                _transfers.NotifyLocalClipboardOverride();
                ClipboardBridge.ClearPendingDataObject();
                break;

            case NativeMethods.WM_RENDERFORMAT:
                if ((uint)wParam.ToInt32() == NativeMethods.CF_HDROP)
                {
                    handled = true;
                    RenderRemoteFilesSync();
                }
                break;

            case NativeMethods.WM_RENDERALLFORMATS:
                handled = true;
                RenderAllFormats();
                break;
        }

        return IntPtr.Zero;
    }

    private void RenderRemoteFilesSync()
    {
        if (Interlocked.Exchange(ref _rendering, 1) == 1) return;
        try
        {
            if (!_transfers.HasRemotePendingFiles && !_transfers.OwnsDelayedClipboard)
                return;

            var task = _transfers.PullRemoteFilesForPasteAsync();
            var files = UiThread.WaitWithPumping(task, TimeSpan.FromMinutes(30));

            if (files is { Count: > 0 })
            {
                ClipboardBridge.SetHDropWhileClipboardOpen(files);
                _transfers.MarkDelayedClipboardOwned(false);
            }
        }
        catch
        {
            // ignore render failures
        }
        finally
        {
            Interlocked.Exchange(ref _rendering, 0);
        }
    }

    private void RenderAllFormats()
    {
        if (!NativeMethods.OpenClipboard(_hwnd)) return;
        try
        {
            NativeMethods.EmptyClipboard();
            if (Interlocked.Exchange(ref _rendering, 1) == 1) return;
            try
            {
                var task = _transfers.PullRemoteFilesForPasteAsync();
                var files = UiThread.WaitWithPumping(task, TimeSpan.FromMinutes(30));
                if (files is { Count: > 0 })
                    ClipboardBridge.SetHDropWhileClipboardOpen(files);
            }
            finally
            {
                Interlocked.Exchange(ref _rendering, 0);
                _transfers.MarkDelayedClipboardOwned(false);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private async Task OnClipboardChangedAsync()
    {
        if (ClipboardBridge.IsSuppressed) return;
        if (!_settings.ShareClipboard) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

        try
        {
            await Task.Delay(120).ConfigureAwait(false);
            if (ClipboardBridge.IsSuppressed) return;

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (ClipboardBridge.IsSuppressed) return;
                if (_transfers.OwnsDelayedClipboard) return;

                if (_settings.TransferFile)
                {
                    var files = ClipboardBridge.TryGetFiles();
                    if (files is { Length: > 0 })
                    {
                        if (files.All(_transfers.IsInboxPath))
                            return;
                        if (ClipboardBridge.IsSameAsLastAppliedFiles(files))
                            return;

                        var key = ClipboardBridge.BuildFilesKey(files);
                        if (key == _lastFilesKey) return;
                        _lastFilesKey = key;

                        await _transfers.AnnounceLocalFilesAsync(files).ConfigureAwait(true);
                        return;
                    }
                }

                var text = ClipboardBridge.TryGetText();
                if (!string.IsNullOrEmpty(text) && text != _lastText)
                {
                    _lastText = text;
                    await _transfers.BroadcastTextAsync(text).ConfigureAwait(true);
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => Stop();
}
