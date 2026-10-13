using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using FlyScreen.Native;
using WinForms = System.Windows.Forms;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 线程安全剪贴板读写，并通过抑制标志避免回环广播。
/// </summary>
public static class ClipboardBridge
{
    private static int _suppress;
    private static string? _lastAppliedFilesKey;
    private static string? _lastAppliedImageKey;
    private static string? _lastAppliedInlineKey;
    private static PendingFileDropDataObject? _pendingDataObject;

    public static bool IsSuppressed => Volatile.Read(ref _suppress) > 0;

    public static void Suppress(int milliseconds = 2000)
    {
        Interlocked.Increment(ref _suppress);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(milliseconds).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _suppress); }
        });
    }

    public static void SetTextSuppressing(string text)
    {
        Suppress(1500);
        try
        {
            System.Windows.Clipboard.Clear();
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // ignore clipboard contention
        }
    }

    public static void SetFilesSuppressing(IReadOnlyList<string> files)
    {
        MarkAppliedFiles(files);
        Suppress(2500);
        try
        {
            var col = new StringCollection();
            foreach (var f in files)
                col.Add(f);
            System.Windows.Clipboard.Clear();
            System.Windows.Clipboard.SetFileDropList(col);
        }
        catch
        {
            // ignore clipboard contention
        }
    }

    public static void MarkAppliedFiles(IReadOnlyList<string> files)
        => _lastAppliedFilesKey = BuildFilesKey(files);

    public static bool IsSameAsLastAppliedFiles(IReadOnlyList<string> files)
        => _lastAppliedFilesKey != null && _lastAppliedFilesKey == BuildFilesKey(files);

    /// <summary>
    /// 用 OLE DataObject 登记待粘贴文件：资源管理器“粘贴”可用；
    /// 拉取在后台进行，GetData 时泵送消息等待，避免卡死 Explorer/本程序。
    /// </summary>
    public static bool OfferPendingFileDrop(
        IReadOnlyList<string> placeholderPaths,
        Func<Task<string[]?>> materializeAsync)
    {
        if (placeholderPaths.Count == 0) return false;

        Suppress(2000);
        try
        {
            var data = new PendingFileDropDataObject(placeholderPaths, materializeAsync);
            _pendingDataObject = data;
            WinForms.Clipboard.Clear();
            // copy:false：仅在真正 GetData（粘贴）时才拉取，其它电脑不会传文件
            WinForms.Clipboard.SetDataObject(data, copy: false);
            MarkAppliedFiles(placeholderPaths);
            return true;
        }
        catch
        {
            _pendingDataObject = null;
            return false;
        }
    }

    /// <summary>Win32 延迟 CF_HDROP 回退（Explorer 右键粘贴可能仍灰）。</summary>
    public static bool OfferDelayedFileDrop(IntPtr ownerHwnd)
    {
        Suppress(1500);
        for (var i = 0; i < 5; i++)
        {
            if (NativeMethods.OpenClipboard(ownerHwnd))
                break;
            Thread.Sleep(40);
            if (i == 4) return false;
        }

        try
        {
            NativeMethods.EmptyClipboard();

            // Preferred DropEffect = COPY，有助于启用粘贴
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
                        Marshal.WriteInt32(p, 1); // DROPEFFECT_COPY
                        NativeMethods.GlobalUnlock(hEffect);
                        NativeMethods.SetClipboardData(fmt, hEffect);
                    }
                }
            }

            NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, IntPtr.Zero);
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static void ClearPendingDataObject()
        => _pendingDataObject = null;

    /// <summary>
    /// 在 WM_RENDERFORMAT 期间（系统已打开剪贴板）写入真实文件列表。
    /// </summary>
    public static bool SetHDropWhileClipboardOpen(IReadOnlyList<string> files)
    {
        var hDrop = CreateHDrop(files);
        if (hDrop == IntPtr.Zero) return false;
        var result = NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hDrop);
        if (result == IntPtr.Zero)
        {
            try { NativeMethods.GlobalFree(hDrop); } catch { /* ignore */ }
            return false;
        }

        MarkAppliedFiles(files);
        Suppress(2500);
        return true;
    }

    public static IntPtr CreateHDrop(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return IntPtr.Zero;

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
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
            (UIntPtr)total);
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
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    public static string? TryGetText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static string[]? TryGetFiles()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList()) return null;
            var list = System.Windows.Clipboard.GetFileDropList();
            var arr = new string[list.Count];
            list.CopyTo(arr, 0);
            return arr;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取剪贴板位图并编码为 PNG（截图 Win+Shift+S 等）。</summary>
    public static byte[]? TryGetPngBytes()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return null;
            var image = System.Windows.Clipboard.GetImage();
            if (image == null) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string BuildImageKey(byte[] png)
    {
        if (png.Length == 0) return "0";
        // 长度 + 头尾采样，避免整图哈希开销
        var head = png.Length > 16 ? BitConverter.ToString(png, 0, 16) : BitConverter.ToString(png);
        var tail = png.Length > 32 ? BitConverter.ToString(png, png.Length - 16, 16) : "";
        return $"{png.Length}:{head}:{tail}";
    }

    public static bool IsSameAsLastAppliedImage(byte[] png)
        => _lastAppliedImageKey != null && _lastAppliedImageKey == BuildImageKey(png);

    public static void SetImageFromPngSuppressing(byte[] png)
        => ApplyInlineSuppressing(ClipboardInlineData.FromPngOnly(png));

    /// <summary>
    /// 采集非文件内联剪贴板：Unicode 文本、HTML、RTF、CSV、位图。
    /// </summary>
    public static ClipboardInlineData? TryCaptureInline()
    {
        try
        {
            string? text = null;
            string? html = null;
            string? rtf = null;
            string? csv = null;
            byte[]? png = null;

            if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            {
                text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
                if (string.IsNullOrEmpty(text)) text = null;
            }

            if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Html))
            {
                html = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Html);
                if (string.IsNullOrEmpty(html)) html = null;
            }

            if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Rtf))
            {
                rtf = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Rtf);
                if (string.IsNullOrEmpty(rtf)) rtf = null;
            }

            if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.CommaSeparatedValue))
            {
                csv = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.CommaSeparatedValue);
                if (string.IsNullOrEmpty(csv)) csv = null;
            }

            png = TryGetPngBytes();

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

    public static bool IsSameAsLastAppliedInline(ClipboardInlineData data)
        => _lastAppliedInlineKey != null && _lastAppliedInlineKey == data.BuildKey();

    public static void MarkAppliedInline(ClipboardInlineData data)
    {
        _lastAppliedInlineKey = data.BuildKey();
        if (data.Png is { Length: > 0 })
            _lastAppliedImageKey = BuildImageKey(data.Png);
    }

    /// <summary>将多格式内联内容写入本机剪贴板（应用会自动选最合适的格式）。</summary>
    public static void ApplyInlineSuppressing(ClipboardInlineData data)
    {
        if (data.IsEmpty) return;
        MarkAppliedInline(data);
        Suppress(2500);
        try
        {
            var obj = new System.Windows.DataObject();
            var any = false;

            if (data.Png is { Length: > 0 })
            {
                using var ms = new MemoryStream(data.Png);
                var decoder = new PngBitmapDecoder(
                    ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                obj.SetImage(frame);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Rtf))
            {
                obj.SetText(data.Rtf, System.Windows.TextDataFormat.Rtf);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Html))
            {
                obj.SetText(data.Html, System.Windows.TextDataFormat.Html);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Csv))
            {
                obj.SetText(data.Csv, System.Windows.TextDataFormat.CommaSeparatedValue);
                any = true;
            }

            if (!string.IsNullOrEmpty(data.Text))
            {
                obj.SetText(data.Text, System.Windows.TextDataFormat.UnicodeText);
                any = true;
            }

            if (!any) return;
            System.Windows.Clipboard.SetDataObject(obj, copy: true);
        }
        catch
        {
            // ignore clipboard contention
        }
    }

    public static string BuildFilesKey(IReadOnlyList<string> files)
    {
        long sum = 0;
        foreach (var f in files)
        {
            try { sum += new FileInfo(f).Length; } catch { /* ignore */ }
        }

        return string.Join("|", files) + "#" + sum;
    }
}
