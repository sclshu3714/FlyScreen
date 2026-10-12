using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
            // copy:false：粘贴时回调 GetData，不在此时阻塞拉取
            WinForms.Clipboard.SetDataObject(data, copy: false);
            MarkAppliedFiles(placeholderPaths);
            // 立刻后台预拉，进度可先显示，粘贴时多半已完成
            data.BeginMaterialize();
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
