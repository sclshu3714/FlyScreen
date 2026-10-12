using System.IO;
using System.Windows;
using System.Windows.Interop;
using FlyScreen.Models;
using FlyScreen.Native;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 监听本机剪贴板变化，同步文本/文件到已连接对端。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FileTransferService _transfers;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private string? _lastText;
    private string? _lastFilesKey;

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
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            _ = OnClipboardChangedAsync();
        return IntPtr.Zero;
    }

    private async Task OnClipboardChangedAsync()
    {
        if (ClipboardBridge.IsSuppressed) return;
        if (!_settings.ShareClipboard) return;

        await Task.Delay(80).ConfigureAwait(false); // 等待剪贴板稳定

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (ClipboardBridge.IsSuppressed) return;

            if (_settings.TransferFile)
            {
                var files = ClipboardBridge.TryGetFiles();
                if (files is { Length: > 0 })
                {
                    var key = string.Join("|", files) + "#" + files.Sum(f =>
                    {
                        try { return new FileInfo(f).Length; } catch { return 0L; }
                    });
                    if (key == _lastFilesKey) return;
                    _lastFilesKey = key;
                    await _transfers.OfferLocalFilesAsync(files).ConfigureAwait(true);
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

    public void Dispose() => Stop();
}
