using System.Windows;
using System.Windows.Interop;
using FlyScreen.Models;
using FlyScreen.Native;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 统一剪贴板：
/// 文件 → 宣告元数据，进入本机后后台异步拉取，完成后写入剪贴板再可粘贴；
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
        if (_hwnd != IntPtr.Zero)
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source = null;
        ClipboardBridge.ClearPendingDataObject();
    }

    /// <summary>
    /// 鼠标已进入本机：异步预取远程文件/内联内容；文件收完前不改剪贴板，不阻塞键鼠。
    /// </summary>
    public void ArmRemoteClipboardForPaste()
    {
        if (!_settings.ShareClipboard) return;

        if (_settings.TransferFile && _transfers.HasRemotePendingFiles)
            _ = ArmFilesAsync();

        if (_transfers.HasRemotePendingInline)
            _ = ArmInlineAsync();
    }

    private async Task ArmFilesAsync()
    {
        if (Interlocked.CompareExchange(ref _arming, 1, 0) != 0) return;

        try
        {
            if (!_settings.TransferFile || !_settings.ShareClipboard) return;
            if (!_transfers.HasRemotePendingFiles) return;

            // 后台拉取：不占位、不写剪贴板，键鼠与本机可正常使用
            var files = await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(false);
            if (files == null || files.Count == 0) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    ClipboardBridge.SetFilesSuppressing(files);
                    _transfers.MarkDelayedClipboardOwned(false);
                    ClipboardBridge.ClearPendingDataObject();
                }
                catch
                {
                    // ignore clipboard contention
                }
            });
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _arming, 0);
        }
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
                // 旧版延迟 CF_HDROP 回退；新路径已在预取完成后写入真实文件列表
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

            // 若已有预取在进行，仅短等；避免在 OLE 回调里长时间堵死
            var task = _transfers.PullRemoteFilesForPasteAsync();
            if (!task.IsCompleted)
                return;

            var files = task.GetAwaiter().GetResult();
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
                if (!task.IsCompleted) return;
                var files = task.GetAwaiter().GetResult();
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
