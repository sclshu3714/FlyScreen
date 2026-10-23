using System.IO;
using System.Windows;
using System.Windows.Interop;
using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Native;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 统一剪贴板：
/// 文件 → 虚拟文件剪贴板（FileGroupDescriptor）；粘贴 GetData 时才接收；
/// 内联 → 进入本机后拉取并写入。
/// 另有轮询备份，避免窗口隐藏/句柄变化时漏掉 WM_CLIPBOARDUPDATE。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private string? _lastFilesKey;
    private string? _lastInlineKey;
    private long _armedFileSequence;
    private string[] _armedPlaceholders = [];
    private int _materialized;
    private int _busy;
    private int _rendering;
    private int _arming;
    private int _inlineArming;
    private int _prefetchStarted;
    private CancellationTokenSource? _pollCts;
    private Window? _window;
    private uint _lastClipboardSeq;

    public ClipboardMonitor(AppSettings settings, FileTransferService transfers)
    {
        _settings = settings;
        _transfers = transfers;
    }

    public void Start(Window window)
    {
        Stop();

        _window = window;
        AttachClipboardHwnd(window);
        window.SourceInitialized += OnWindowSourceInitialized;

        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            AttachClipboardHwnd(window);
    }

    private void AttachClipboardHwnd(Window window)
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
                NativeMethods.RemoveClipboardFormatListener(_hwnd);
            _source?.RemoveHook(WndProc);

            var helper = new WindowInteropHelper(window);
            helper.EnsureHandle();
            _hwnd = helper.Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            NativeMethods.AddClipboardFormatListener(_hwnd);
        }
        catch
        {
            // ignore
        }
    }

    public void Stop()
    {
        try { _pollCts?.Cancel(); } catch { /* ignore */ }
        _pollCts?.Dispose();
        _pollCts = null;

        if (_window != null)
        {
            _window.SourceInitialized -= OnWindowSourceInitialized;
            _window = null;
        }

        if (_hwnd != IntPtr.Zero)
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
        ClipboardBridge.ClearPendingDataObject();
        _armedPlaceholders = [];
    }

    /// <summary>
    /// 鼠标已进入本机 / 本机收到宣告：只挂占位剪贴板点亮「粘贴」，不开始网络接收。
    /// </summary>
    public void ArmRemoteClipboardForPaste()
    {
        if (!_settings.ShareClipboard) return;

        if (_settings.TransferFile && _transfers.HasRemotePendingFiles)
            ArmFilesForLazyPaste();

        if (_transfers.HasRemotePendingInline)
            _ = ArmInlineAsync();
    }

    /// <summary>不再预取：接收必须由真正粘贴触发。</summary>
    public void PrefetchForLikelyPaste()
    {
        // intentionally empty
    }

    /// <summary>
    /// 虚拟文件剪贴板由资源管理器 GetData 触发拉取，无需拦截 Ctrl+V。
    /// </summary>
    public bool TryHandlePasteHotkey() => false;

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (!_settings.ShareClipboard) continue;
                if (ClipboardBridge.IsSuppressed || ClipboardBridge.IsOfferingPending) continue;

                // 用剪贴板序号检测变化，避免漏掉 WM_CLIPBOARDUPDATE（托盘/无焦点）
                var seq = ClipboardBridge.GetClipboardSequenceNumber();
                if (seq == 0 || seq == _lastClipboardSeq) continue;

                await ProcessClipboardChangeAsync(force: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>挂虚拟文件剪贴板；粘贴 GetData 时才 Pull。</summary>
    private void ArmFilesForLazyPaste()
    {
        if (Interlocked.CompareExchange(ref _arming, 1, 0) != 0) return;

        try
        {
            if (!_settings.TransferFile || !_settings.ShareClipboard) return;
            if (!_transfers.HasRemotePendingFiles) return;

            var seq = _transfers.GetRemotePendingSequence();
            if (seq != 0 && seq == _armedFileSequence
                && ClipboardBridge.HasVirtualFilePaste
                && _transfers.OwnsDelayedClipboard
                && Volatile.Read(ref _materialized) == 0)
                return;

            if (_transfers.GetRemotePendingEntries().Count == 0) return;

            void Offer()
            {
                try
                {
                    var ok = ClipboardBridge.OfferVirtualFilePaste(MaterializePasteEntriesAsync);
                    if (!ok)
                    {
                        _armedFileSequence = 0;
                        _ = RetryArmFilesAsync();
                        return;
                    }

                    _armedFileSequence = seq;
                    _armedPlaceholders = [];
                    Interlocked.Exchange(ref _materialized, 0);
                    Interlocked.Exchange(ref _prefetchStarted, 0);
                    _transfers.MarkDelayedClipboardOwned(true);
                }
                catch
                {
                    _armedFileSequence = 0;
                    _ = RetryArmFilesAsync();
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess())
                Offer();
            else
                dispatcher.Invoke(Offer);
        }
        finally
        {
            Interlocked.Exchange(ref _arming, 0);
        }
    }

    private async Task RetryArmFilesAsync()
    {
        try
        {
            await Task.Delay(300).ConfigureAwait(false);
            if (!_transfers.HasRemotePendingFiles) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            await dispatcher.InvokeAsync(ArmFilesForLazyPaste);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>虚拟剪贴板 GetData：此时才网络接收，返回 FileGroupDescriptor 明细。</summary>
    private async Task<IReadOnlyList<ClipboardPasteEntry>?> MaterializePasteEntriesAsync()
    {
        _transfers.SetPasteMergeRoots(null);
        var files = await _transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(false);
        if (files is { Count: > 0 })
            Interlocked.Exchange(ref _materialized, 1);

        var entries = _transfers.GetLastPasteEntries();
        if (entries is { Count: > 0 })
            return entries;

        // 回退：仅有顶层路径时也构造成条目
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
                _ = ProcessClipboardChangeAsync(force: false);
                break;

            case NativeMethods.WM_DESTROYCLIPBOARD:
                if (ClipboardBridge.IsSuppressed || ClipboardBridge.IsOfferingPending)
                    break;
                _transfers.NotifyLocalClipboardOverride();
                ClipboardBridge.ClearPendingDataObject();
                _armedFileSequence = 0;
                _armedPlaceholders = [];
                Interlocked.Exchange(ref _materialized, 0);
                Interlocked.Exchange(ref _prefetchStarted, 0);
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
            if (!task.IsCompleted)
                UiThread.WaitWithPumping(task, TimeSpan.FromMinutes(30));

            var files = task.GetAwaiter().GetResult();
            if (files is { Count: > 0 })
            {
                ClipboardBridge.SetHDropWhileClipboardOpen(files);
                _armedPlaceholders = files.ToArray();
                Interlocked.Exchange(ref _materialized, 1);
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
                if (!task.IsCompleted)
                    UiThread.WaitWithPumping(task, TimeSpan.FromMinutes(30));
                var files = task.GetAwaiter().GetResult();
                if (files is { Count: > 0 })
                {
                    ClipboardBridge.SetHDropWhileClipboardOpen(files);
                    _armedPlaceholders = files.ToArray();
                    Interlocked.Exchange(ref _materialized, 1);
                }
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

    private async Task ProcessClipboardChangeAsync(bool force)
    {
        if (ClipboardBridge.IsSuppressed || ClipboardBridge.IsOfferingPending) return;
        if (!_settings.ShareClipboard) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

        try
        {
            // Explorer 延迟 HDROP 需稍等再读
            await Task.Delay(force ? 200 : 150).ConfigureAwait(false);
            if (ClipboardBridge.IsSuppressed || ClipboardBridge.IsOfferingPending) return;

            var seqNow = ClipboardBridge.GetClipboardSequenceNumber();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            string[]? files = null;
            ClipboardInlineData? inline = null;
            await dispatcher.InvokeAsync(() =>
            {
                if (ClipboardBridge.IsSuppressed || ClipboardBridge.IsOfferingPending) return;

                if (_settings.TransferFile)
                {
                    files = ClipboardBridge.TryGetFiles();
                    if (files is { Length: > 0 })
                    {
                        if (!files.All(_transfers.IsInboxPath))
                        {
                            // 本机真实复制：清掉远程占位状态，允许宣告
                            if (_transfers.OwnsDelayedClipboard)
                            {
                                _transfers.NotifyLocalClipboardOverride();
                                ClipboardBridge.ClearPendingDataObject();
                                ClipboardBridge.ClearLastAppliedFilesKey();
                                _armedFileSequence = 0;
                                _armedPlaceholders = [];
                                Interlocked.Exchange(ref _materialized, 0);
                            }

                            return;
                        }

                        files = null; // 占位路径，不宣告
                    }
                }

                inline = ClipboardBridge.TryCaptureInline();
            }).Task.ConfigureAwait(false);

            if (ClipboardBridge.IsSuppressed) return;

            if (files is { Length: > 0 })
            {
                var key = ClipboardBridge.BuildFilesKey(files);
                // 同一剪贴板序号且同 key 才跳过；用户再次「复制」会换序号
                if (!force && key == _lastFilesKey) return;
                if (force && key == _lastFilesKey && seqNow == _lastClipboardSeq) return;

                ClipboardBridge.ClearLastAppliedFilesKey();
                if (await _transfers.AnnounceLocalFilesAsync(files).ConfigureAwait(false))
                {
                    _lastFilesKey = key;
                    _lastClipboardSeq = seqNow;
                }

                return;
            }

            if (inline == null || inline.IsEmpty)
            {
                if (seqNow != 0)
                    _lastClipboardSeq = seqNow;
                return;
            }

            if (ClipboardBridge.IsSameAsLastAppliedInline(inline)) return;

            var inlineKey = inline.BuildKey();
            if (inlineKey == _lastInlineKey && seqNow == _lastClipboardSeq) return;

            await _transfers.AnnounceLocalInlineAsync(inline).ConfigureAwait(false);
            _lastInlineKey = inlineKey;
            _lastClipboardSeq = seqNow;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => Stop();
}
