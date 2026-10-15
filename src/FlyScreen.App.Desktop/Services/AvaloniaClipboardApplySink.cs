using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>Avalonia 剪贴板写入（Mac/Linux 桌面）。</summary>
public sealed class AvaloniaClipboardApplySink : IClipboardApplySink
{
    private long _suppressUntilMs;

    public void Suppress(int ms)
        => _suppressUntilMs = Environment.TickCount64 + Math.Max(0, ms);

    public bool IsSuppressed => Environment.TickCount64 < _suppressUntilMs;

    public void ApplyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Suppress(1500);
        _ = SetTextAsync(text);
    }

    public void ApplyInline(ClipboardInlineData data)
    {
        if (data.IsEmpty) return;
        Suppress(2500);
        // Avalonia 跨平台剪贴板以文本为主；富文本降级为纯文本
        if (!string.IsNullOrEmpty(data.Text))
            _ = SetTextAsync(data.Text);
        else if (!string.IsNullOrEmpty(data.Html))
            _ = SetTextAsync(data.Html);
        else if (!string.IsNullOrEmpty(data.Csv))
            _ = SetTextAsync(data.Csv);
        else if (!string.IsNullOrEmpty(data.Rtf))
            _ = SetTextAsync(data.Rtf);
    }

    public async Task<string?> TryGetTextAsync()
    {
        try
        {
            var clip = GetClipboard();
            if (clip == null) return null;
            return await clip.GetTextAsync().ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task SetTextAsync(string text)
    {
        try
        {
            var clip = GetClipboard();
            if (clip == null) return;
            await clip.SetTextAsync(text).ConfigureAwait(true);
        }
        catch
        {
            // 剪贴板争用忽略
        }
    }

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow?.Clipboard;
        return null;
    }
}
