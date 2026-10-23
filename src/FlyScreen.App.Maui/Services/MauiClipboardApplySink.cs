using FlyScreen.Services.Clipboard;

namespace FlyScreen.AppMaui.Services;

/// <summary>跨平台文本剪贴板写入（MAUI Clipboard API）。Windows 必须在 UI 线程访问。</summary>
public sealed class MauiClipboardApplySink : IClipboardApplySink
{
    private long _suppressUntilMs;

    public void Suppress(int ms)
        => _suppressUntilMs = Environment.TickCount64 + Math.Max(0, ms);

    public bool IsSuppressed => Environment.TickCount64 < _suppressUntilMs;

    public void ApplyText(string text)
    {
        if (string.IsNullOrEmpty(text) || IsSuppressed) return;
        Suppress(1500);
        _ = SetTextAsync(text);
    }

    public void ApplyInline(ClipboardInlineData data)
    {
        if (data.IsEmpty || IsSuppressed) return;
        Suppress(2500);
        var text = !string.IsNullOrEmpty(data.Text) ? data.Text
            : !string.IsNullOrEmpty(data.Html) ? data.Html
            : !string.IsNullOrEmpty(data.Csv) ? data.Csv
            : data.Rtf;
        if (!string.IsNullOrEmpty(text))
            _ = SetTextAsync(text);
    }

    public async Task<string?> TryGetTextAsync()
    {
        try
        {
            // Windows 剪贴板 COM 要求 UI 线程；后台轮询须切回主线程
            if (MainThread.IsMainThread)
                return await ReadTextOnCurrentThreadAsync().ConfigureAwait(true);

            return await MainThread.InvokeOnMainThreadAsync(ReadTextOnCurrentThreadAsync)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadTextOnCurrentThreadAsync()
    {
        try
        {
            if (!Clipboard.Default.HasText)
                return null;
            return await Clipboard.Default.GetTextAsync().ConfigureAwait(true);
        }
        catch
        {
            // 剪贴板被占用 / 格式不支持等
            return null;
        }
    }

    private static async Task SetTextAsync(string text)
    {
        try
        {
            if (MainThread.IsMainThread)
            {
                await WriteTextOnCurrentThreadAsync(text).ConfigureAwait(true);
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() => WriteTextOnCurrentThreadAsync(text))
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task WriteTextOnCurrentThreadAsync(string text)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(text).ConfigureAwait(true);
        }
        catch
        {
            // ignore contention
        }
    }
}
