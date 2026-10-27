using System.Text;
using FlyScreen.Native;

namespace FlyScreen.Win32Clipboard;

/// <summary>
/// Win32 文件剪贴板读写（WPF / Maui-Win 共用）：延迟 CF_HDROP、剪贴板序号。
/// </summary>
public static class Win32FileClipboard
{
    public static uint GetSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public static bool IsHDropAvailable()
        => NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);

    /// <summary>读取 CF_HDROP（强制物化 Explorer 延迟渲染），含文件与文件夹路径。</summary>
    public static IReadOnlyList<string>? TryReadPaths()
    {
        if (!IsHDropAvailable()) return null;

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

                return paths.Count > 0 ? paths : null;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        return null;
    }

    /// <summary>写入 CF_HDROP + Preferred DropEffect=COPY（真实路径，可含已填满的文件夹）。</summary>
    public static bool TryOfferPaths(IntPtr ownerHwnd, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;

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
                            System.Runtime.InteropServices.Marshal.WriteInt32(p, 1);
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
        var headerSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DROPFILES>();
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
            System.Runtime.InteropServices.Marshal.StructureToPtr(drop, ptr, false);
            System.Runtime.InteropServices.Marshal.Copy(pathBytes, 0, ptr + headerSize, pathBytes.Length);
            return hGlobal;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }
    }
}
