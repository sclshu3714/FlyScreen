using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FlyScreen.Models;
using FlyScreen.Native;

namespace FlyScreen.Win32Clipboard;

/// <summary>
/// 虚拟文件剪贴板：FileGroupDescriptorW + FileContents。
/// QueryGetData 高亮粘贴；GetData 时再拉取。避免 CF_HDROP 的 0KB / 灰粘贴两难。
/// 使用 PreserveSig：DV_E_FORMATETC 等为正常 HRESULT，勿抛 COMException（调试器会停）。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("A3F8C2E1-9B4D-4F2A-8E7C-1D5B6A0C9F21")]
public sealed class VirtualFilePasteDataObject : IOleDataObject
{
    public const string FormatFileGroupDescriptorW = "FileGroupDescriptorW";
    public const string FormatFileContents = "FileContents";
    public const string FormatPreferredDropEffect = "Preferred DropEffect";

    private const int S_OK = 0;
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FILENOTFOUND = unchecked((int)0x80070002);

    private readonly Func<Task<IReadOnlyList<ClipboardPasteEntry>?>> _materializeAsync;
    private readonly object _gate = new();
    private Task<IReadOnlyList<ClipboardPasteEntry>?>? _pullTask;
    private ClipboardPasteEntry[]? _entries;

    private static readonly short s_fmtDescriptor = ToClipFormat(NativeMethods.RegisterClipboardFormat(FormatFileGroupDescriptorW));
    private static readonly short s_fmtContents = ToClipFormat(NativeMethods.RegisterClipboardFormat(FormatFileContents));
    private static readonly short s_fmtDropEffect = ToClipFormat(NativeMethods.RegisterClipboardFormat(FormatPreferredDropEffect));

    public VirtualFilePasteDataObject(Func<Task<IReadOnlyList<ClipboardPasteEntry>?>> materializeAsync)
    {
        _materializeAsync = materializeAsync
                            ?? throw new ArgumentNullException(nameof(materializeAsync));
    }

    public static bool TrySetClipboard(VirtualFilePasteDataObject data)
    {
        try
        {
            OleInitialize(IntPtr.Zero);
            var hr = OleSetClipboard(data);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
    }

    #region IOleDataObject

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return E_NOTIMPL;
    }

    public int DUnadvise(int connection) => E_NOTIMPL;

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return E_NOTIMPL;
    }

    public int EnumFormatEtc(DATADIR direction, out IEnumFORMATETC enumFormatEtc)
    {
        if (direction != DATADIR.DATADIR_GET)
        {
            enumFormatEtc = new FormatEnumerator([]);
            return S_OK;
        }

        enumFormatEtc = new FormatEnumerator(
        [
            MakeFmt(s_fmtDropEffect, -1, TYMED.TYMED_HGLOBAL),
            MakeFmt(s_fmtDescriptor, -1, TYMED.TYMED_HGLOBAL),
            // lindex=-1 表示“有 FileContents”；实际 GetData 带具体下标
            MakeFmt(s_fmtContents, -1, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM)
        ]);
        return S_OK;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        return DATA_S_SAMEFORMATETC;
    }

    public int GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = new STGMEDIUM { tymed = TYMED.TYMED_NULL, unionmember = IntPtr.Zero, pUnkForRelease = null };
        var cf = format.cfFormat;

        if (cf == s_fmtDropEffect)
        {
            if (!HasTymed(format.tymed, TYMED.TYMED_HGLOBAL))
                return DV_E_TYMED;
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = AllocHGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY
            return S_OK;
        }

        if (cf == s_fmtDescriptor)
        {
            if (!HasTymed(format.tymed, TYMED.TYMED_HGLOBAL))
                return DV_E_TYMED;

            var entries = EnsureEntries();
            if (entries.Length == 0)
                return E_FAIL;

            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildFileGroupDescriptor(entries);
            return S_OK;
        }

        if (cf == s_fmtContents)
            return GetFileContents(ref format, out medium);

        return DV_E_FORMATETC;
    }

    public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => E_NOTIMPL;

    public int QueryGetData(ref FORMATETC format)
    {
        var cf = format.cfFormat;
        if (cf == s_fmtDropEffect && HasTymed(format.tymed, TYMED.TYMED_HGLOBAL))
            return S_OK;
        if (cf == s_fmtDescriptor && HasTymed(format.tymed, TYMED.TYMED_HGLOBAL))
            return S_OK; // 仅探测：不拉取
        if (cf == s_fmtContents
            && (HasTymed(format.tymed, TYMED.TYMED_HGLOBAL) || HasTymed(format.tymed, TYMED.TYMED_ISTREAM)))
            return S_OK;
        return DV_E_FORMATETC;
    }

    public int SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // 只读：OLE/Explorer 偶发 SetData；接受并释放即可
        if (release)
            ReleaseStgMedium(ref medium);
        return S_OK;
    }

    #endregion

    private int GetFileContents(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = new STGMEDIUM { tymed = TYMED.TYMED_NULL, unionmember = IntPtr.Zero, pUnkForRelease = null };

        var wantHGlobal = HasTymed(format.tymed, TYMED.TYMED_HGLOBAL);
        var wantStream = HasTymed(format.tymed, TYMED.TYMED_ISTREAM);
        if (!wantHGlobal && !wantStream)
            return DV_E_TYMED;

        var entries = EnsureEntries();
        var index = format.lindex;
        if (index < 0 || index >= entries.Length)
            return E_INVALIDARG;

        var entry = entries[index];
        byte[] bytes;
        if (entry.IsDirectory)
        {
            // 目录无内容流：0 字节，避免 Explorer 当成致命错误
            bytes = [];
        }
        else
        {
            if (!File.Exists(entry.FullPath))
                return E_FILENOTFOUND;
            try { bytes = File.ReadAllBytes(entry.FullPath); }
            catch { return E_FAIL; }
        }

        // 优先 HGLOBAL（与 Query/Enum 一致，且无托管 IStream RCW 生命周期问题）
        if (wantHGlobal)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = AllocHGlobal(bytes);
            return S_OK;
        }

        // 仅要 ISTREAM：CreateStreamOnHGlobal，由 COM 释放 HGLOBAL
        var h = AllocHGlobal(bytes);
        var hr = CreateStreamOnHGlobal(h, fDeleteOnRelease: true, out var stream);
        if (hr < 0 || stream is null)
        {
            NativeMethods.GlobalFree(h);
            return hr < 0 ? hr : E_FAIL;
        }

        medium.tymed = TYMED.TYMED_ISTREAM;
        medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
        // GetComInterfaceForObject 已 AddRef；释放本地 RCW 多余引用
        Marshal.ReleaseComObject(stream);
        return S_OK;
    }

    private ClipboardPasteEntry[] EnsureEntries()
    {
        lock (_gate)
        {
            if (_entries is { Length: > 0 })
                return _entries;

            _pullTask ??= Task.Run(async () =>
            {
                try { return await _materializeAsync().ConfigureAwait(false); }
                catch { return null; }
            });
        }

        var task = _pullTask!;
        var deadline = Environment.TickCount64 + (long)TimeSpan.FromMinutes(30).TotalMilliseconds;
        while (!task.IsCompleted)
        {
            if (Environment.TickCount64 > deadline)
                break;
            try
            {
                if (task.Wait(50))
                    break;
            }
            catch
            {
                break;
            }

            NativeMethods.PumpWaitingMessages();
        }

        lock (_gate)
        {
            if (_entries is { Length: > 0 })
                return _entries;

            if (task.IsCompletedSuccessfully && task.Result is { Count: > 0 } list)
            {
                _entries = list
                    .Where(e => !string.IsNullOrWhiteSpace(e.RelativePath))
                    .Select(e => new ClipboardPasteEntry(
                        e.RelativePath.Replace('/', '\\'),
                        e.FullPath,
                        e.IsDirectory,
                        e.IsDirectory ? 0 : (File.Exists(e.FullPath) ? new FileInfo(e.FullPath).Length : e.Size)))
                    .ToArray();
            }
            else
            {
                _entries = [];
            }

            return _entries;
        }
    }

    private static bool HasTymed(TYMED have, TYMED want) => (have & want) != 0;

    private static short ToClipFormat(uint fmt) => unchecked((short)(ushort)fmt);

    private static FORMATETC MakeFmt(short cf, int lindex, TYMED tymed) => new()
    {
        cfFormat = cf,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        tymed = tymed,
        ptd = IntPtr.Zero
    };

    private static IntPtr AllocHGlobal(byte[] data)
    {
        var h = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
            (UIntPtr)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) throw new OutOfMemoryException();
        if (data.Length == 0) return h;

        var p = NativeMethods.GlobalLock(h);
        try { Marshal.Copy(data, 0, p, data.Length); }
        finally { NativeMethods.GlobalUnlock(h); }
        return h;
    }

    private static IntPtr BuildFileGroupDescriptor(ClipboardPasteEntry[] entries)
    {
        var fdSize = Marshal.SizeOf<FILEDESCRIPTORW>();
        if (fdSize != 592)
            throw new InvalidOperationException($"FILEDESCRIPTORW size={fdSize}, expected 592");

        var total = 4 + entries.Length * fdSize;
        var h = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (UIntPtr)total);
        if (h == IntPtr.Zero) throw new OutOfMemoryException();

        var ptr = NativeMethods.GlobalLock(h);
        try
        {
            Marshal.WriteInt32(ptr, entries.Length);
            var cursor = IntPtr.Add(ptr, 4);
            var now = DateTime.Now.ToFileTime();

            foreach (var e in entries)
            {
                var name = e.RelativePath;
                if (name.Length >= 260)
                    name = name[..259];

                var fd = new FILEDESCRIPTORW
                {
                    dwFlags = FD_UNICODE | FD_WRITESTIME | FD_FILESIZE | FD_ATTRIBUTES | FD_PROGRESSUI,
                    dwFileAttributes = e.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                    ftLastWriteTime = now,
                    nFileSizeHigh = (uint)((ulong)e.Size >> 32),
                    nFileSizeLow = (uint)(e.Size & 0xFFFFFFFF),
                    cFileName = name
                };
                Marshal.StructureToPtr(fd, cursor, false);
                cursor = IntPtr.Add(cursor, fdSize);
            }
        }
        finally
        {
            NativeMethods.GlobalUnlock(h);
        }

        return h;
    }

    private const uint FD_ATTRIBUTES = 0x00000004;
    private const uint FD_WRITESTIME = 0x00000020;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint FD_PROGRESSUI = 0x00004000;
    private const uint FD_UNICODE = 0x80000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public int sizelCx;
        public int sizelCy;
        public int pointlX;
        public int pointlY;
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(IOleDataObject? pDataObj);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(
        IntPtr hGlobal,
        [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease,
        out IStream? ppstm);

    private sealed class FormatEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _items;
        private int _index;

        public FormatEnumerator(FORMATETC[] items) => _items = items;

        public void Clone(out IEnumFORMATETC newEnum)
            => newEnum = new FormatEnumerator((FORMATETC[])_items.Clone()) { _index = _index };

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            var n = 0;
            while (n < celt && _index < _items.Length)
            {
                rgelt[n++] = _items[_index++];
            }

            if (pceltFetched is { Length: > 0 })
                pceltFetched[0] = n;
            return n == celt ? 0 : 1;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public int Skip(int celt)
        {
            if (_index + celt > _items.Length)
            {
                _index = _items.Length;
                return 1;
            }

            _index += celt;
            return 0;
        }
    }
}

/// <summary>
/// OLE IDataObject（PreserveSig）。勿用 ComTypes.IDataObject：失败 HRESULT 会变成 COMException。
/// </summary>
[ComImport]
[Guid("0000010e-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDataObject
{
    [PreserveSig]
    int GetData(ref FORMATETC pFormatetc, out STGMEDIUM pMedium);

    [PreserveSig]
    int GetDataHere(ref FORMATETC pFormatetc, ref STGMEDIUM pMedium);

    [PreserveSig]
    int QueryGetData(ref FORMATETC pFormatetc);

    [PreserveSig]
    int GetCanonicalFormatEtc(ref FORMATETC pFormatetcIn, out FORMATETC pFormatetcOut);

    [PreserveSig]
    int SetData(ref FORMATETC pFormatetc, ref STGMEDIUM pMedium, [MarshalAs(UnmanagedType.Bool)] bool fRelease);

    [PreserveSig]
    int EnumFormatEtc(DATADIR dwDirection, out IEnumFORMATETC ppenumFormatEtc);

    [PreserveSig]
    int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink pAdvSink, out int pdwConnection);

    [PreserveSig]
    int DUnadvise(int dwConnection);

    [PreserveSig]
    int EnumDAdvise(out IEnumSTATDATA? ppenumAdvise);
}
