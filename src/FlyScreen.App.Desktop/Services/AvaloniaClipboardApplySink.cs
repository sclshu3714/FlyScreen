using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>Avalonia 剪贴板读写（Mac/Linux 桌面）：尽量对齐 Text/Html/Rtf/Csv/Png/Files。</summary>
public sealed class AvaloniaClipboardApplySink : IClipboardApplySink
{
    private static readonly string[] HtmlFormats =
    [
        "text/html", "HTML Format", "public.html", "org.chromium.webui-source-url+html"
    ];

    private static readonly string[] RtfFormats =
    [
        "text/rtf", "Rich Text Format", "public.rtf"
    ];

    private static readonly string[] CsvFormats =
    [
        "text/csv", "Csv", "CommaSeparatedValue"
    ];

    private static readonly string[] PngFormats =
    [
        "image/png", "PNG", "public.png"
    ];

    private long _suppressUntilMs;
    private string? _lastAppliedKey;

    public void Suppress(int ms)
        => _suppressUntilMs = Environment.TickCount64 + Math.Max(0, ms);

    public bool IsSuppressed => Environment.TickCount64 < _suppressUntilMs;

    public bool IsSameAsLastApplied(ClipboardInlineData data)
        => _lastAppliedKey != null && _lastAppliedKey == data.BuildKey();

    public void ApplyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Suppress(1500);
        _ = SetTextAsync(text);
    }

    public void ApplyInline(ClipboardInlineData data)
    {
        if (data.IsEmpty) return;
        _lastAppliedKey = data.BuildKey();
        Suppress(2500);
        _ = ApplyInlineAsync(data);
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

    /// <summary>采集本机内联剪贴板（文本/HTML/RTF/CSV/PNG）。</summary>
    public async Task<ClipboardInlineData?> TryCaptureInlineAsync()
    {
        try
        {
            var clip = GetClipboard();
            if (clip == null) return null;

            string[] formats;
            try { formats = await clip.GetFormatsAsync().ConfigureAwait(true); }
            catch { formats = []; }

            string? text = null;
            try { text = await clip.GetTextAsync().ConfigureAwait(true); }
            catch { /* ignore */ }
            if (string.IsNullOrEmpty(text)) text = null;

            var html = await TryGetStringAsync(clip, formats, HtmlFormats).ConfigureAwait(true);
            var rtf = await TryGetStringAsync(clip, formats, RtfFormats).ConfigureAwait(true);
            var csv = await TryGetStringAsync(clip, formats, CsvFormats).ConfigureAwait(true);
            var png = await TryGetPngAsync(clip, formats).ConfigureAwait(true);

            var data = new ClipboardInlineData
            {
                Text = text,
                Html = html,
                Rtf = rtf,
                Csv = csv,
                Png = png
            };
            return data.IsEmpty ? null : data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取剪贴板中的本地文件/文件夹路径（宣告用）。</summary>
    public async Task<IReadOnlyList<string>?> TryGetFilePathsAsync()
    {
        try
        {
            var clip = GetClipboard();
            if (clip == null) return null;

            var data = await clip.GetDataAsync(DataFormats.Files).ConfigureAwait(true);
            if (data is null) return null;

            var paths = new List<string>();
            if (data is IEnumerable<IStorageItem> items)
            {
                foreach (var item in items)
                {
                    var path = item.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }
            }
            else if (data is IStorageItem one)
            {
                var path = one.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths.Count > 0 ? paths : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task ApplyInlineAsync(ClipboardInlineData data)
    {
        try
        {
            var clip = GetClipboard();
            if (clip == null) return;

            if (data.Png is { Length: > 0 })
            {
                await ApplyPngAsync(clip, data).ConfigureAwait(true);
                return;
            }

            var dataObject = new DataObject();
            var any = false;

            if (!string.IsNullOrEmpty(data.Text))
            {
                dataObject.Set(DataFormats.Text, data.Text);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Html))
            {
                dataObject.Set("text/html", data.Html);
                dataObject.Set("public.html", data.Html);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Rtf))
            {
                dataObject.Set("text/rtf", data.Rtf);
                dataObject.Set("public.rtf", data.Rtf);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Csv))
            {
                dataObject.Set("text/csv", data.Csv);
                any = true;
            }

            if (any)
            {
                await clip.SetDataObjectAsync(dataObject).ConfigureAwait(true);
                return;
            }

            var fallback = PreferText(data);
            if (!string.IsNullOrEmpty(fallback))
                await clip.SetTextAsync(fallback).ConfigureAwait(true);
        }
        catch
        {
            var fallback = PreferText(data);
            if (!string.IsNullOrEmpty(fallback))
                await SetTextAsync(fallback).ConfigureAwait(true);
        }
    }

    private static string? PreferText(ClipboardInlineData data)
        => !string.IsNullOrEmpty(data.Text) ? data.Text
            : !string.IsNullOrEmpty(data.Html) ? data.Html
            : !string.IsNullOrEmpty(data.Csv) ? data.Csv
            : data.Rtf;

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

    private static async Task ApplyPngAsync(IClipboard clip, ClipboardInlineData data)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flyscreen-clip");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"clip-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, data.Png!).ConfigureAwait(true);

        // 优先：原生 PNG 字节（Linux/Mac 多数应用可识别）
        try
        {
            var dataObject = new DataObject();
            dataObject.Set("image/png", data.Png!);
            dataObject.Set("PNG", data.Png!);
            dataObject.Set("public.png", data.Png!);
            if (!string.IsNullOrEmpty(data.Text))
                dataObject.Set(DataFormats.Text, data.Text);
            await clip.SetDataObjectAsync(dataObject).ConfigureAwait(true);
            return;
        }
        catch
        {
            // fall through to Files
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk
            && desk.MainWindow?.StorageProvider is { } storage)
        {
            var file = await storage.TryGetFileFromPathAsync(path).ConfigureAwait(true);
            if (file != null)
            {
                var dataObject = new DataObject();
                dataObject.Set(DataFormats.Files, new IStorageItem[] { file });
                if (!string.IsNullOrEmpty(data.Text))
                    dataObject.Set(DataFormats.Text, data.Text);
                await clip.SetDataObjectAsync(dataObject).ConfigureAwait(true);
                return;
            }
        }

        if (!string.IsNullOrEmpty(data.Text))
            await SetTextAsync(data.Text).ConfigureAwait(true);
        else
            await SetTextAsync(path).ConfigureAwait(true);
    }

    private static async Task<string?> TryGetStringAsync(
        IClipboard clip, string[] available, string[] candidates)
    {
        foreach (var format in candidates)
        {
            if (available.Length > 0
                && !available.Any(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var obj = await clip.GetDataAsync(format).ConfigureAwait(true);
                switch (obj)
                {
                    case string s when !string.IsNullOrEmpty(s):
                        return s;
                    case byte[] bytes when bytes.Length > 0:
                        return System.Text.Encoding.UTF8.GetString(bytes);
                    case MemoryStream ms:
                        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static async Task<byte[]?> TryGetPngAsync(IClipboard clip, string[] available)
    {
        foreach (var format in PngFormats)
        {
            if (available.Length > 0
                && !available.Any(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var obj = await clip.GetDataAsync(format).ConfigureAwait(true);
                switch (obj)
                {
                    case byte[] bytes when bytes.Length > 0:
                        return bytes;
                    case MemoryStream ms:
                        return ms.ToArray();
                    case Stream stream:
                    {
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms).ConfigureAwait(true);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow?.Clipboard;
        return null;
    }
}
