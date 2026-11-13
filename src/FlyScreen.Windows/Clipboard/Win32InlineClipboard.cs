using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Native;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.Win32Clipboard;

/// <summary>
/// Win32 写入内联剪贴板（不依赖前台窗口）。
/// WinRT <c>Clipboard.SetContent</c> 在被控/后台常失败；仅写 PNG 私有格式时多数应用无法粘贴截图。
/// </summary>
public static class Win32InlineClipboard
{
    private static readonly uint FmtHtml = NativeMethods.RegisterClipboardFormat("HTML Format");
    private static readonly uint FmtRtf = NativeMethods.RegisterClipboardFormat("Rich Text Format");
    private static readonly uint FmtCsv = NativeMethods.RegisterClipboardFormat("Csv");
    private static readonly uint FmtPng = NativeMethods.RegisterClipboardFormat("PNG");

    public static bool TryApply(ClipboardInlineData data)
    {
        if (data.IsEmpty) return false;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(30 + attempt * 20);
                continue;
            }

            try
            {
                NativeMethods.EmptyClipboard();
                var any = false;
                var imageOk = false;

                if (!string.IsNullOrEmpty(data.Text)
                    && SetUnicodeText(data.Text))
                    any = true;

                if (!string.IsNullOrEmpty(data.Html)
                    && FmtHtml != 0
                    && SetUtf8Format(FmtHtml, EnsureCfHtml(data.Html)))
                    any = true;

                if (!string.IsNullOrEmpty(data.Rtf)
                    && FmtRtf != 0
                    && SetUtf8Format(FmtRtf, data.Rtf))
                    any = true;

                if (!string.IsNullOrEmpty(data.Csv)
                    && FmtCsv != 0
                    && SetUtf8Format(FmtCsv, data.Csv))
                    any = true;

                if (data.Png is { Length: > 0 })
                {
                    // 粘贴截图靠 CF_DIB；仅 PNG 私有格式时记事本/画图/Office 常粘不出
                    imageOk = SetDibFromPng(data.Png);
                    if (FmtPng != 0)
                        SetBytes(FmtPng, data.Png);
                }

                // 纯图且 DIB 失败：返回 false，让上层走 WinRT SetBitmap
                if (!any && data.Png is { Length: > 0 } && !imageOk)
                    return false;

                return any || imageOk;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return false;
    }

    private static bool SetDibFromPng(byte[] png)
    {
        try
        {
            using var inMs = new MemoryStream(png, writable: false);
            using var src = new Bitmap(inMs);
            using var dibBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dibBmp))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }

            using var outMs = new MemoryStream();
            dibBmp.Save(outMs, ImageFormat.Bmp);
            var file = outMs.ToArray();
            if (file.Length <= 14) return false;

            // BMP 文件头 14 字节，CF_DIB 只要 BITMAPINFOHEADER 起
            var dib = new byte[file.Length - 14];
            Buffer.BlockCopy(file, 14, dib, 0, dib.Length);
            return SetBytes(NativeMethods.CF_DIB, dib);
        }
        catch
        {
            return false;
        }
    }

    private static bool SetUnicodeText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return SetBytes(NativeMethods.CF_UNICODETEXT, bytes);
    }

    private static bool SetUtf8Format(uint format, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        return SetBytes(format, bytes);
    }

    private static bool SetBytes(uint format, byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        var hMem = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
            (UIntPtr)(uint)bytes.Length);
        if (hMem == IntPtr.Zero) return false;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(hMem);
            return false;
        }

        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }

        if (NativeMethods.SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(hMem);
            return false;
        }

        return true;
    }

    /// <summary>CF_HTML 需要 Version/StartHTML 头；已有则原样写入。</summary>
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
}
