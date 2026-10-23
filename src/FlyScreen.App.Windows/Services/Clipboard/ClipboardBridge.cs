using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using FlyScreen.Models;
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
    private static int _offeringPending;
    private static int _virtualPasteArmed;

    public static bool IsSuppressed => Volatile.Read(ref _suppress) > 0;

    /// <summary>是否仍挂着懒粘贴用的 OLE FileDrop（尚未被其它程序覆盖）。</summary>
    public static bool HasPendingDataObject => _pendingDataObject != null;

    /// <summary>是否已挂虚拟文件剪贴板（FileGroupDescriptor）。</summary>
    public static bool HasVirtualFilePaste => Volatile.Read(ref _virtualPasteArmed) != 0;

    /// <summary>正在挂接占位剪贴板（Clear/SetDataObject 期间为 true）。</summary>
    public static bool IsOfferingPending => Volatile.Read(ref _offeringPending) != 0;

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
    /// 虚拟文件剪贴板：FileGroupDescriptorW + FileContents。
    /// QueryGetData 高亮粘贴；GetData 时才拉取真实内容（微软推荐，避免 CF_HDROP 0KB）。
    /// </summary>
    public static bool OfferVirtualFilePaste(
        Func<Task<IReadOnlyList<ClipboardPasteEntry>?>> materializeAsync)
    {
        Suppress(2500);
        Interlocked.Exchange(ref _offeringPending, 1);
        try
        {
            var data = new FlyScreen.Win32Clipboard.VirtualFilePasteDataObject(materializeAsync);
            for (var i = 0; i < 8; i++)
            {
                if (FlyScreen.Win32Clipboard.VirtualFilePasteDataObject.TrySetClipboard(data))
                {
                    _pendingDataObject = null;
                    Interlocked.Exchange(ref _virtualPasteArmed, 1);
                    _lastAppliedFilesKey = "virtual-paste:" + Guid.NewGuid().ToString("N");
                    return true;
                }

                Thread.Sleep(40 + i * 20);
            }

            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _offeringPending, 0);
        }
    }

    /// <summary>
    /// 旧版 OLE FileDrop 占位（保留供兼容）；新路径请用 <see cref="OfferVirtualFilePaste"/>。
    /// </summary>
    public static bool OfferPendingFileDrop(
        IReadOnlyList<string> placeholderPaths,
        Func<Task<string[]?>> materializeAsync)
    {
        if (placeholderPaths.Count == 0) return false;

        Suppress(2500);
        Interlocked.Exchange(ref _offeringPending, 1);
        try
        {
            var data = new PendingFileDropDataObject(placeholderPaths, materializeAsync);

            Exception? last = null;
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    WinForms.Clipboard.SetDataObject(data, copy: false);
                    _pendingDataObject = data;
                    MarkAppliedFiles(placeholderPaths);
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(40 + i * 20);
                }
            }

            _pendingDataObject = null;
            System.Diagnostics.Debug.WriteLine("OfferPendingFileDrop failed: " + last?.Message);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _offeringPending, 0);
        }
    }

    /// <summary>
    /// Win32 立即写入 CF_HDROP（真实占位路径）。资源管理器据此点亮「粘贴」。
    /// 内容可稍后写入同一路径；勿用延迟 NULL（Win10/11 上粘贴常保持灰色）。
    /// </summary>
    public static bool OfferImmediateFileDrop(IntPtr ownerHwnd, IReadOnlyList<string> placeholderPaths)
    {
        if (placeholderPaths.Count == 0 || ownerHwnd == IntPtr.Zero) return false;

        Suppress(2500);
        Interlocked.Exchange(ref _offeringPending, 1);
        try
        {
            for (var i = 0; i < 8; i++)
            {
                if (!NativeMethods.OpenClipboard(ownerHwnd))
                {
                    Thread.Sleep(40 + i * 20);
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
                                Marshal.WriteInt32(p, 1); // DROPEFFECT_COPY
                                NativeMethods.GlobalUnlock(hEffect);
                                NativeMethods.SetClipboardData(fmt, hEffect);
                            }
                        }
                    }

                    var hDrop = CreateHDrop(placeholderPaths);
                    if (hDrop == IntPtr.Zero) return false;
                    if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hDrop) == IntPtr.Zero)
                    {
                        try { NativeMethods.GlobalFree(hDrop); } catch { /* ignore */ }
                        return false;
                    }

                    _pendingDataObject = null;
                    MarkAppliedFiles(placeholderPaths);
                    return NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _offeringPending, 0);
        }
    }

    /// <summary>将已拉取的文件/文件夹合并进占位路径（保持 CF_HDROP 顶层路径不变）。</summary>
    public static void MergeFilesIntoPlaceholders(IReadOnlyList<string> materialized, IReadOnlyList<string> placeholders)
    {
        if (materialized.Count == 0 || placeholders.Count == 0) return;

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in materialized)
        {
            if (File.Exists(path) || Directory.Exists(path))
                byName[Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))] = path;
        }

        for (var i = 0; i < placeholders.Count; i++)
        {
            var dest = placeholders[i];
            var name = Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!byName.TryGetValue(name, out var src))
            {
                if (i < materialized.Count
                    && (File.Exists(materialized[i]) || Directory.Exists(materialized[i])))
                    src = materialized[i];
                else
                    continue;
            }

            try
            {
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Directory.Exists(src))
                {
                    CopyDirectoryReplace(src, dest);
                    continue;
                }

                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(dest))
                    File.Delete(dest);
                if (Directory.Exists(dest))
                    Directory.Delete(dest, recursive: true);

                if (!NativeMethods.CreateHardLink(dest, src, IntPtr.Zero))
                    File.Copy(src, dest, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(src))
                        File.Copy(src, dest, overwrite: true);
                }
                catch { /* ignore */ }
            }
        }
    }

    private static void CopyDirectoryReplace(string sourceDir, string destDir)
    {
        if (File.Exists(destDir))
            File.Delete(destDir);
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void ClearPendingDataObject()
    {
        _pendingDataObject = null;
        Interlocked.Exchange(ref _virtualPasteArmed, 0);
    }

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
        // 优先 Win32：资源管理器「复制」常用延迟 CF_HDROP，WPF/WinForms 有时读不到
        var win32 = TryGetFilesViaWin32HDrop();
        if (win32 is { Length: > 0 }) return win32;

        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var list = System.Windows.Clipboard.GetFileDropList();
                var arr = new string[list.Count];
                list.CopyTo(arr, 0);
                return arr;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            if (WinForms.Clipboard.ContainsFileDropList())
            {
                var list = WinForms.Clipboard.GetFileDropList();
                var arr = new string[list.Count];
                list.CopyTo(arr, 0);
                return arr;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>用 DragQueryFile 强制物化延迟 CF_HDROP（Explorer 复制）。</summary>
    public static string[]? TryGetFilesViaWin32HDrop()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            return null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20 + attempt * 15);
                continue;
            }

            try
            {
                var hDrop = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
                if (hDrop == IntPtr.Zero) return null;

                var count = NativeMethods.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                if (count == 0) return null;

                var paths = new List<string>((int)count);
                var sb = new StringBuilder(520);
                for (uint i = 0; i < count; i++)
                {
                    sb.Clear();
                    var len = NativeMethods.DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    if (len == 0) continue;
                    var path = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }

                return paths.Count > 0 ? paths.ToArray() : null;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return null;
    }

    public static uint GetClipboardSequenceNumber()
        => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>本机用户复制后清除「已应用」标记，避免挡住再次宣告。</summary>
    public static void ClearLastAppliedFilesKey()
        => _lastAppliedFilesKey = null;

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
        => ClipboardInlineData.BuildImageKey(png);

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
