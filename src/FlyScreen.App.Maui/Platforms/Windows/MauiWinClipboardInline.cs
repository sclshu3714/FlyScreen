#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using FlyScreen.Services.Clipboard;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinDataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using WinDataPackageView = Windows.ApplicationModel.DataTransfer.DataPackageView;
using WinHtmlFormatHelper = Windows.ApplicationModel.DataTransfer.HtmlFormatHelper;
using WinStandardDataFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// Maui-Win：采集/写入内联剪贴板（对齐 Win ClipboardBridge：Text/Html/Rtf/Csv/Png）。
/// </summary>
internal static class MauiWinClipboardInline
{
    /// <summary>与 WPF DataFormats.CommaSeparatedValue / CFSTR_CSV 一致。</summary>
    private const string FormatCsv = "Csv";
    private const string FormatRtfLegacy = "Rich Text Format";

    public static async Task<ClipboardInlineData?> TryCaptureAsync()
    {
        try
        {
            var content = WinClipboard.GetContent();
            if (content == null) return null;

            string? text = null;
            string? html = null;
            string? rtf = null;
            string? csv = null;
            byte[]? png = null;

            if (content.Contains(WinStandardDataFormats.Text))
            {
                text = await content.GetTextAsync();
                if (string.IsNullOrEmpty(text)) text = null;
            }

            if (content.Contains(WinStandardDataFormats.Html))
            {
                html = await content.GetHtmlFormatAsync();
                if (string.IsNullOrEmpty(html)) html = null;
            }

            if (content.Contains(WinStandardDataFormats.Rtf))
            {
                try
                {
                    rtf = await content.GetRtfAsync();
                    if (string.IsNullOrEmpty(rtf)) rtf = null;
                }
                catch
                {
                    rtf = await TryGetStringFormatAsync(content, FormatRtfLegacy).ConfigureAwait(true);
                }
            }
            else
            {
                rtf = await TryGetStringFormatAsync(content, FormatRtfLegacy).ConfigureAwait(true);
            }

            csv = await TryGetStringFormatAsync(content, FormatCsv).ConfigureAwait(true);

            if (content.Contains(WinStandardDataFormats.Bitmap))
                png = await TryReadBitmapAsPngAsync(content).ConfigureAwait(true);

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

    public static async Task ApplyAsync(ClipboardInlineData data)
    {
        if (data.IsEmpty) return;

        var package = new WinDataPackage();
        var any = false;

        if (data.Png is { Length: > 0 })
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.Png.AsBuffer());
            stream.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            any = true;
        }

        if (!string.IsNullOrEmpty(data.Text))
        {
            package.SetText(data.Text);
            any = true;
        }

        if (!string.IsNullOrEmpty(data.Html))
        {
            try
            {
                var html = data.Html.Contains("Version:", StringComparison.OrdinalIgnoreCase)
                    ? data.Html
                    : WinHtmlFormatHelper.CreateHtmlFormat(data.Html);
                package.SetHtmlFormat(html);
                any = true;
            }
            catch
            {
                // ignore
            }
        }

        if (!string.IsNullOrEmpty(data.Rtf))
        {
            try
            {
                package.SetRtf(data.Rtf);
                any = true;
            }
            catch
            {
                try
                {
                    package.SetData(FormatRtfLegacy, data.Rtf);
                    any = true;
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (!string.IsNullOrEmpty(data.Csv))
        {
            try
            {
                package.SetData(FormatCsv, data.Csv);
                any = true;
            }
            catch
            {
                // ignore
            }
        }

        if (!any)
        {
            var fallback = PreferText(data);
            if (string.IsNullOrEmpty(fallback)) return;
            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default
                .SetTextAsync(fallback)
                .ConfigureAwait(true);
            return;
        }

        WinClipboard.SetContent(package);
        WinClipboard.Flush();
    }

    private static string? PreferText(ClipboardInlineData data)
        => !string.IsNullOrEmpty(data.Text) ? data.Text
            : !string.IsNullOrEmpty(data.Html) ? data.Html
            : !string.IsNullOrEmpty(data.Csv) ? data.Csv
            : data.Rtf;

    private static async Task<string?> TryGetStringFormatAsync(WinDataPackageView content, string formatId)
    {
        try
        {
            if (!content.Contains(formatId)) return null;
            var obj = await content.GetDataAsync(formatId);
            switch (obj)
            {
                case string s:
                    return string.IsNullOrEmpty(s) ? null : s;
                case IRandomAccessStream stream:
                {
                    var bytes = await ReadAllBytesAsync(stream).ConfigureAwait(true);
                    if (bytes.Length == 0) return null;
                    // RTF/CSV 多为 ANSI 或 UTF-8；优先 UTF-8，失败再 Latin1
                    var text = Encoding.UTF8.GetString(bytes);
                    if (text.Contains('\uFFFD')
                        && formatId.Equals(FormatRtfLegacy, StringComparison.OrdinalIgnoreCase))
                        text = Encoding.Latin1.GetString(bytes);
                    return string.IsNullOrEmpty(text) ? null : text;
                }
                default:
                    return obj?.ToString();
            }
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryReadBitmapAsPngAsync(WinDataPackageView content)
    {
        try
        {
            var bmpRef = await content.GetBitmapAsync();
            if (bmpRef == null) return null;

            using var ras = await bmpRef.OpenReadAsync();
            if (ras == null || ras.Size == 0) return null;

            var raw = await ReadAllBytesAsync(ras).ConfigureAwait(true);
            if (raw.Length == 0) return null;

            if (raw.Length >= 8
                && raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47)
                return raw;

            var inStream = new InMemoryRandomAccessStream();
            await inStream.WriteAsync(raw.AsBuffer());
            inStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inStream);
            using var soft = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            if (soft == null) return null;

            var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetSoftwareBitmap(soft);
            await encoder.FlushAsync();
            outStream.Seek(0);
            return await ReadAllBytesAsync(outStream).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        if (size == 0) return [];
        var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        reader.Dispose();
        return bytes;
    }
}
#endif
