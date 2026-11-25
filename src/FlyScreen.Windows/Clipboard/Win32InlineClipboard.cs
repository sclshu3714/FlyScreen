using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Native;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.Win32Clipboard;

/// <summary>
/// Win32 读写内联剪贴板（不依赖前台窗口）。
/// WinRT <c>Clipboard.SetContent</c> 在被控/后台常失败；仅写 PNG 私有格式时多数应用无法粘贴截图。
/// WPF/WinRT 的 Bitmap API 也读不到仅有 <c>PNG</c> 私有格式的剪贴板（浏览器「复制图片」常见）。
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

        // 先在剪贴板外准备 DIB，避免 EmptyClipboard 后转换失败留下空板
        byte[]? dib = null;
        if (data.Png is { Length: > 0 })
            dib = TryEncodeDib24FromPng(data.Png);

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
                    if (dib is { Length: > 0 })
                        imageOk = SetBytes(NativeMethods.CF_DIB, dib);
                    if (FmtPng != 0)
                        SetBytes(FmtPng, data.Png);
                }

                // 纯图且 DIB 失败：返回 false，让上层走 WinRT/WPF SetBitmap
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

    /// <summary>从本机图片文件编码 PNG（预览「复制图片」常只放 CF_HDROP 的 jpg）。</summary>
    public static byte[]? TryEncodePngFromImageFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (!ClipboardImagePaths.IsImageFileName(path)) return null;

            using var src = new Bitmap(path);
            using var ms = new MemoryStream();
            src.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 写入 CF_HDROP +（可选）CF_DIB/PNG，对齐本机「复制图片文件」：既可粘到文件夹，也可粘到对话框/文档。
    /// </summary>
    public static bool TryOfferPathsWithOptionalImage(IReadOnlyList<string> paths, byte[]? png)
    {
        if (paths.Count == 0) return false;

        byte[]? dib = null;
        if (png is { Length: > 0 })
            dib = TryEncodeDib24FromPng(png);

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

                var fmt = NativeMethods.RegisterClipboardFormat("Preferred DropEffect");
                if (fmt != 0)
                {
                    var hEffect = NativeMethods.GlobalAlloc(
                        NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (UIntPtr)4);
                    if (hEffect != IntPtr.Zero)
                    {
                        var p = NativeMethods.GlobalLock(hEffect);
                        if (p != IntPtr.Zero)
                        {
                            Marshal.WriteInt32(p, 1);
                            NativeMethods.GlobalUnlock(hEffect);
                            NativeMethods.SetClipboardData(fmt, hEffect);
                        }
                    }
                }

                var hDrop = CreateHDrop(paths);
                if (hDrop == IntPtr.Zero) return false;
                if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hDrop) == IntPtr.Zero)
                {
                    try { NativeMethods.GlobalFree(hDrop); } catch { /* ignore */ }
                    return false;
                }

                if (dib is { Length: > 0 })
                    SetBytes(NativeMethods.CF_DIB, dib);
                if (png is { Length: > 0 } && FmtPng != 0)
                    SetBytes(FmtPng, png);

                return NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return false;
    }

    private static IntPtr CreateHDrop(IReadOnlyList<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            sb.Append(f);
            sb.Append('\0');
        }

        sb.Append('\0');
        var pathBytes = Encoding.Unicode.GetBytes(sb.ToString());
        var headerSize = Marshal.SizeOf<NativeMethods.DROPFILES>();
        var total = headerSize + pathBytes.Length;
        var hGlobal = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (UIntPtr)total);
        if (hGlobal == IntPtr.Zero) return IntPtr.Zero;

        var ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var drop = new NativeMethods.DROPFILES
            {
                pFiles = (uint)headerSize,
                pt = default,
                fNC = 0,
                fWide = 1
            };
            Marshal.StructureToPtr(drop, ptr, false);
            Marshal.Copy(pathBytes, 0, ptr + headerSize, pathBytes.Length);
            return hGlobal;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }
    }

    /// <summary>
    /// 采集剪贴板图片为 PNG：先读 <c>PNG</c> 私有格式，再回退 <c>CF_DIB</c>。
    /// 供 WPF/WinRT ContainsImage/Bitmap 漏采时使用。
    /// </summary>
    public static byte[]? TryCapturePng()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20 + attempt * 15);
                continue;
            }

            try
            {
                if (FmtPng != 0
                    && NativeMethods.IsClipboardFormatAvailable(FmtPng))
                {
                    var png = TryReadFormatBytes(FmtPng);
                    if (png is { Length: > 8 }
                        && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47)
                        return png;
                }

                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
                {
                    var dib = TryReadFormatBytes(NativeMethods.CF_DIB);
                    if (dib is { Length: > 40 })
                    {
                        var png = TryEncodePngFromDib(dib);
                        if (png is { Length: > 0 })
                            return png;
                    }
                }

                return null;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return null;
    }

    private static byte[]? TryEncodeDib24FromPng(byte[] png)
    {
        try
        {
            using var inMs = new MemoryStream(png, writable: false);
            using var src = new Bitmap(inMs);
            // 24bpp BI_RGB：画图/Office/浏览器粘贴兼容性最好
            using var dibBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dibBmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.White);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }

            using var outMs = new MemoryStream();
            dibBmp.Save(outMs, ImageFormat.Bmp);
            var file = outMs.ToArray();
            if (file.Length <= 14) return null;

            // BMP 文件头 14 字节，CF_DIB 只要 BITMAPINFOHEADER 起
            var dib = new byte[file.Length - 14];
            Buffer.BlockCopy(file, 14, dib, 0, dib.Length);
            return dib;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryEncodePngFromDib(byte[] dib)
    {
        try
        {
            var biSize = BitConverter.ToInt32(dib, 0);
            if (biSize < 40 || biSize > dib.Length) return null;

            var biBitCount = BitConverter.ToInt16(dib, 14);
            var biCompression = BitConverter.ToInt32(dib, 16);
            var biClrUsed = BitConverter.ToInt32(dib, 32);

            var colorTableBytes = 0;
            if (biCompression == 3 /* BI_BITFIELDS */)
                colorTableBytes = biBitCount == 32 ? 16 : 12;
            else if (biBitCount <= 8)
            {
                var colors = biClrUsed != 0 ? biClrUsed : 1 << biBitCount;
                colorTableBytes = colors * 4;
            }

            var bfOffBits = 14 + biSize + colorTableBytes;
            if (bfOffBits > 14 + dib.Length) return null;

            using var ms = new MemoryStream(14 + dib.Length);
            Span<byte> hdr = stackalloc byte[14];
            hdr[0] = (byte)'B';
            hdr[1] = (byte)'M';
            BitConverter.TryWriteBytes(hdr[2..6], 14 + dib.Length);
            BitConverter.TryWriteBytes(hdr[10..14], bfOffBits);
            ms.Write(hdr);
            ms.Write(dib);
            ms.Position = 0;

            using var bmp = new Bitmap(ms);
            using var outMs = new MemoryStream();
            bmp.Save(outMs, ImageFormat.Png);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadFormatBytes(uint format)
    {
        var hMem = NativeMethods.GetClipboardData(format);
        if (hMem == IntPtr.Zero) return null;

        var size = NativeMethods.GlobalSize(hMem);
        if (size == UIntPtr.Zero) return null;
        var len = checked((int)size.ToUInt64());
        if (len <= 0 || len > 64 * 1024 * 1024) return null;

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
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
