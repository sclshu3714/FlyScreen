using System.Windows;
using System.Windows.Interop;
using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Native;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 统一剪贴板：
/// 文件 → 宣告元数据，进入本机后登记延迟粘贴，真正粘贴时拉取；
/// 内联（文本/HTML/RTF/CSV/图片）→ 宣告，进入本机后再拉取并写入。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private string? _lastFilesKey;
    private string? _lastInlineKey;
    private int _busy;
    private int _rendering;
    private int _arming;
    private int _inlineArming;

    public ClipboardMonitor(AppSettings settings, FileTransferService transfers)
    {
        _settings = settings;
        _transfers = transfers;
        _transfers.RemoteFilesAnnounced += OnRemoteAnnounced;
        _transfers.RemoteInlineAnnounced += OnRemoteAnnounced;
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
        _transfers.RemoteFilesAnnounced -= OnRemoteAnnounced;
        _transfers.RemoteInlineAnnounced -= OnRemoteAnnounced;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source = null;
        ClipboardBridge.ClearPendingDataObject();
    }

    private void OnRemoteAnnounced()
    {
        // 只登记元数据；等鼠标进入本机后再 ArmRemoteClipboardForPaste()
    }

    /// <summary>
    /// 鼠标已进入本机：文件登记延迟剪贴板；内联内容拉取后写入。
    /// </summary>
    public void ArmRemoteClipboardForPaste()
    {
        if (!_settings.ShareClipboard) return;

        if (_settings.TransferFile && _transfers.HasRemotePendingFiles && !_transfers.OwnsDelayedClipboard)
            ArmFiles();

        if (_transfers.HasRemotePendingInline)
            _ = ArmInlineAsync();
    }

    private void ArmFiles()
    {
        if (Interlocked.CompareExchange(ref _arming, 1, 0) != 0) return;

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
                ArmFilesCore();
            else
                dispatcher.Invoke(ArmFilesCore);
        }
        finally
        {
            Interlocked.Exchange(ref _arming, 0);
        }
    }

    private void ArmFilesCore()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!_transfers.HasRemotePendingFiles) return;
        if (_transfers.OwnsDelayedClipboard) return;

        var names = _transfers.GetRemotePendingFileNames();
        if (names.Count == 0) return;

        var placeholders = _transfers.CreatePastePlaceholders(names);
        if (placeholders.Count == 0) return;

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
    }

    private async Task ArmInlineAsync()
    {
        if (Interlocked.CompareExchange(ref _inlineArming, 1, 0) != 0) return;
        try
        {
            if (!_transfers.HasRemotePendingInline) return;

            var data = await _transfers.PullRemoteInlineForPasteAsync().ConfigureAwait(false);
            if (data == null || data.IsEmpty) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            await dispatcher.InvokeAsync(() =>
            {
                ClipboardBridge.ApplyInlineSuppressing(data);
                _lastInlineKey = data.BuildKey();
            });
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _inlineArming, 0);
        }
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
            // ignore
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

                // 1) 文件优先
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

                // 2) 内联：文本 + HTML + RTF + CSV + 图片（可组合）
                var inline = ClipboardBridge.TryCaptureInline();
                if (inline == null || inline.IsEmpty) return;
                if (ClipboardBridge.IsSameAsLastAppliedInline(inline)) return;

                var inlineKey = inline.BuildKey();
                if (inlineKey == _lastInlineKey) return;
                _lastInlineKey = inlineKey;

                await _transfers.AnnounceLocalInlineAsync(inline).ConfigureAwait(true);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => Stop();
}
