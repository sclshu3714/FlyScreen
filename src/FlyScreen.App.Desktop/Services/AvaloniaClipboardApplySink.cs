using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// Avalonia 剪贴板读写（Mac/Linux）：对齐 Win 多格式并存——
/// Text/Html/Rtf/Csv/Png/Files 一次写入，采集覆盖常见 MIME / UTI。
/// </summary>
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
        "text/csv", "Csv", "CommaSeparatedValue", "public.comma-separated-values-text"
    ];

    private static readonly string[] PngFormats =
    [
        "image/png", "PNG", "public.png"
    ];

    private static readonly string[] RasterFormats =
    [
        "image/png", "PNG", "public.png",
        "image/jpeg", "image/jpg", "public.jpeg", "public.jpg",
        "image/bmp", "image/x-bmp", "public.bmp",
        "image/gif", "public.gif",
        "image/tiff", "public.tiff",
        "image/webp"
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
        _ = ApplyInlineAndRematchAsync(data);
    }

    /// <summary>
    /// 将本地路径挂到系统剪贴板 Files（可选附带 PNG），供 Finder/文件管理器与文档粘贴。
    /// </summary>
    public Task<bool> ApplyFilesAsync(IReadOnlyList<string> paths, byte[]? png = null)
        => ApplyFilesInternalAsync(paths, png);

    private async Task ApplyInlineAndRematchAsync(ClipboardInlineData data)
    {
        await ApplyInlineAsync(data).ConfigureAwait(true);
        try
        {
            var roundtrip = await TryCaptureInlineAsync().ConfigureAwait(true);
            if (roundtrip != null && !roundtrip.IsEmpty)
                _lastAppliedKey = roundtrip.BuildKey();
        }
        catch
        {
            // ignore
        }
    }

    private async Task<bool> ApplyFilesInternalAsync(IReadOnlyList<string> paths, byte[]? png)
    {
        if (paths.Count == 0) return false;
        Suppress(2500);
        try
        {
            var clip = GetClipboard();
            if (clip == null) return false;

            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desk
                || desk.MainWindow?.StorageProvider is not { } storage)
                return false;

            var items = await BuildStorageItemsAsync(storage, paths).ConfigureAwait(true);
            if (items.Count == 0) return false;

            var dataObject = new DataObject();
            dataObject.Set(DataFormats.Files, items.ToArray());
            if (png is { Length: > 0 })
                SetImageFormats(dataObject, png);

            if (png is { Length: > 0 })
                _lastAppliedKey = ClipboardInlineData.FromPngOnly(png).BuildKey();

            await clip.SetDataObjectAsync(dataObject).ConfigureAwait(true);
            return true;
        }
        catch
        {
            return false;
        }
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
            if (!string.IsNullOrEmpty(html))
                html = StripCfHtmlEnvelope(html);

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

            var dataObject = new DataObject();
            var any = false;

            if (!string.IsNullOrEmpty(data.Text))
            {
                dataObject.Set(DataFormats.Text, data.Text);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Html))
            {
                var html = EnsureCfHtml(data.Html);
                dataObject.Set("text/html", data.Html);
                dataObject.Set("public.html", data.Html);
                dataObject.Set("HTML Format", html);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Rtf))
            {
                dataObject.Set("text/rtf", data.Rtf);
                dataObject.Set("public.rtf", data.Rtf);
                dataObject.Set("Rich Text Format", data.Rtf);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Csv))
            {
                dataObject.Set("text/csv", data.Csv);
                dataObject.Set("Csv", data.Csv);
                any = true;
            }

            if (data.Png is { Length: > 0 })
            {
                SetImageFormats(dataObject, data.Png);
                // 同时挂临时 PNG 文件，对齐本机「复制图片」可粘到文件夹
                var fileItem = await TryCreateTempPngStorageItemAsync(data.Png).ConfigureAwait(true);
                if (fileItem != null)
                    dataObject.Set(DataFormats.Files, new IStorageItem[] { fileItem });
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

    private static void SetImageFormats(DataObject dataObject, byte[] png)
    {
        dataObject.Set("image/png", png);
        dataObject.Set("PNG", png);
        dataObject.Set("public.png", png);
    }

    private static async Task<IStorageFile?> TryCreateTempPngStorageItemAsync(byte[] png)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "flyscreen-clip");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"clip-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(path, png).ConfigureAwait(true);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk
                && desk.MainWindow?.StorageProvider is { } storage)
                return await storage.TryGetFileFromPathAsync(path).ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static async Task<List<IStorageItem>> BuildStorageItemsAsync(
        IStorageProvider storage, IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                if (File.Exists(path))
                {
                    var file = await storage.TryGetFileFromPathAsync(path).ConfigureAwait(true);
                    if (file != null) items.Add(file);
                }
                else if (Directory.Exists(path))
                {
                    var folder = await storage.TryGetFolderFromPathAsync(path).ConfigureAwait(true);
                    if (folder != null) items.Add(folder);
                }
            }
            catch
            {
                // skip one
            }
        }

        return items;
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

    private static async Task<string?> TryGetStringAsync(
        IClipboard clip, string[] available, string[] candidates)
    {
        foreach (var format in candidates)
        {
            // available 为空时仍逐个尝试：部分桌面 GetFormats 不完整
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
                        return Encoding.UTF8.GetString(bytes);
                    case MemoryStream ms:
                        return Encoding.UTF8.GetString(ms.ToArray());
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
        foreach (var format in RasterFormats)
        {
            if (available.Length > 0
                && !available.Any(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var raw = await TryReadBytesAsync(clip, format).ConfigureAwait(true);
                if (raw is not { Length: > 0 }) continue;

                if (IsPng(raw))
                    return raw;

                var png = TryTranscodeRasterToPng(raw);
                if (png is { Length: > 0 })
                    return png;
            }
            catch
            {
                // try next
            }
        }

        // 格式列表不准时再盲试 PNG
        if (available.Length > 0)
        {
            foreach (var format in PngFormats)
            {
                try
                {
                    var raw = await TryReadBytesAsync(clip, format).ConfigureAwait(true);
                    if (raw is { Length: > 0 } && IsPng(raw))
                        return raw;
                }
                catch { /* next */ }
            }
        }

        return null;
    }

    private static async Task<byte[]?> TryReadBytesAsync(IClipboard clip, string format)
    {
        var obj = await clip.GetDataAsync(format).ConfigureAwait(true);
        return obj switch
        {
            byte[] bytes when bytes.Length > 0 => bytes,
            MemoryStream ms => ms.ToArray(),
            Stream stream => await ReadStreamAsync(stream).ConfigureAwait(true),
            _ => null
        };
    }

    private static async Task<byte[]> ReadStreamAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(true);
        return ms.ToArray();
    }

    private static bool IsPng(byte[] raw)
        => raw.Length >= 8
           && raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47;

    private static byte[]? TryTranscodeRasterToPng(byte[] raw)
    {
        try
        {
            using var inMs = new MemoryStream(raw, writable: false);
            using var bmp = new Bitmap(inMs);
            using var outMs = new MemoryStream();
            bmp.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CF_HTML 信封；已有则原样。</summary>
    private static string EnsureCfHtml(string html)
    {
        if (html.Contains("Version:", StringComparison.OrdinalIgnoreCase)
            && html.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase))
            return html;

        const string prefix =
            "Version:0.9\r\nStartHTML:<<<<<<<1\r\nEndHTML:<<<<<<<2\r\nStartFragment:<<<<<<<3\r\nEndFragment:<<<<<<<4\r\n";
        const string startFrag = "<!--StartFragment-->";
        const string endFrag = "<!--EndFragment-->";
        var body = startFrag + html + endFrag;
        var startHtml = prefix.Length;
        var startFragment = startHtml + startFrag.Length;
        var endFragment = startHtml + body.Length - endFrag.Length;
        var endHtml = startHtml + body.Length;

        return prefix
            .Replace("<<<<<<<1", startHtml.ToString("D8"))
            .Replace("<<<<<<<2", endHtml.ToString("D8"))
            .Replace("<<<<<<<3", startFragment.ToString("D8"))
            .Replace("<<<<<<<4", endFragment.ToString("D8"))
            + body;
    }

    private static string StripCfHtmlEnvelope(string html)
    {
        if (!html.Contains("StartFragment:", StringComparison.OrdinalIgnoreCase))
            return html;

        var startTag = "<!--StartFragment-->";
        var endTag = "<!--EndFragment-->";
        var i = html.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        var j = html.IndexOf(endTag, StringComparison.OrdinalIgnoreCase);
        if (i >= 0 && j > i)
            return html[(i + startTag.Length)..j];

        return html;
    }

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow?.Clipboard;
        return null;
    }
}
