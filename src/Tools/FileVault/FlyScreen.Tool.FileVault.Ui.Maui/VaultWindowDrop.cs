using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

/// <summary>
/// 在保险箱窗口上接收资源管理器拖入的文件和文件夹。
/// WinUI 把放置目标挂在内容子窗口上，顶层窗口上的 AllowDrop 改不了禁止光标。
/// </summary>
internal sealed class VaultWindowDrop
{
    private const int AlreadyRegistered = unchecked((int)0x80040101);
    private const int NotInitialized = unchecked((int)0x800401F0);
    private const uint CopyEffect = 1;
    private DropTarget? _target;
    private readonly List<IntPtr> _hwnds = new();

    public void Attach(Window? window, Action<IReadOnlyList<string>> import)
    {
        if (!OperatingSystem.IsWindows() || window == null)
            return;
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return;

        _target ??= new DropTarget(import);
        var seen = new HashSet<nint>();
        Register(_target, hwnd, seen);
        Native.EnumChildWindows(hwnd, (child, _) =>
        {
            Register(_target, child, seen);
            return true;
        }, IntPtr.Zero);
    }

    public void Detach()
    {
        foreach (var hwnd in _hwnds)
        {
            try { Native.RevokeDragDrop(hwnd); } catch { /* ignore */ }
        }
        _hwnds.Clear();
        _target = null;
    }

    private void Register(DropTarget target, IntPtr hwnd, HashSet<nint> seen)
    {
        if (hwnd == IntPtr.Zero || !seen.Add(hwnd))
            return;
        var hr = Native.RegisterDragDrop(hwnd, target.Pointer);
        if (hr == NotInitialized)
        {
            Native.OleInitialize(IntPtr.Zero);
            hr = Native.RegisterDragDrop(hwnd, target.Pointer);
        }
        if (hr == AlreadyRegistered)
        {
            Native.RevokeDragDrop(hwnd);
            hr = Native.RegisterDragDrop(hwnd, target.Pointer);
        }
        if (hr == 0 && !_hwnds.Contains(hwnd))
            _hwnds.Add(hwnd);
    }

    private static IntPtr Hwnd(Window window)
    {
        var native = window.Handler?.PlatformView;
        if (native == null)
            return IntPtr.Zero;
        try
        {
            var windowNative = Type.GetType("WinRT.Interop.WindowNative, Microsoft.Windows.SDK.NET")
                               ?? Type.GetType("WinRT.Interop.WindowNative, WinRT.Interop");
            var getHandle = windowNative?.GetMethod("GetWindowHandle", BindingFlags.Public | BindingFlags.Static);
            if (getHandle?.Invoke(null, [native]) is IntPtr handle && handle != IntPtr.Zero)
                return handle;
        }
        catch
        {
            // 再试窗口属性
        }

        var value = native.GetType().GetProperty("WindowHandle")?.GetValue(native);
        return value switch
        {
            IntPtr p => p,
            long l => new IntPtr(l),
            _ => IntPtr.Zero
        };
    }

    /// <summary>
    /// 手写 IDropTarget。托管接口会把 POINTL 和效果指针错位，写不进 DROPEFFECT，光标就一直是禁止。
    /// </summary>
    private sealed class DropTarget
    {
        private readonly Action<IReadOnlyList<string>> _import;
        private readonly Native.QueryInterfaceFn _queryInterface;
        private readonly Native.AddRefFn _addRef;
        private readonly Native.ReleaseFn _release;
        private readonly Native.DragEnterFn _dragEnter;
        private readonly Native.DragOverFn _dragOver;
        private readonly Native.DragLeaveFn _dragLeave;
        private readonly Native.DropFn _drop;
        private readonly IntPtr _vtable;
        private readonly IntPtr _self;
        private int _refs = 1;

        public DropTarget(Action<IReadOnlyList<string>> import)
        {
            _import = import;
            _queryInterface = QueryInterface;
            _addRef = AddRef;
            _release = Release;
            _dragEnter = DragEnter;
            _dragOver = DragOver;
            _dragLeave = DragLeave;
            _drop = Drop;

            var slots = new IntPtr[7];
            slots[0] = Marshal.GetFunctionPointerForDelegate(_queryInterface);
            slots[1] = Marshal.GetFunctionPointerForDelegate(_addRef);
            slots[2] = Marshal.GetFunctionPointerForDelegate(_release);
            slots[3] = Marshal.GetFunctionPointerForDelegate(_dragEnter);
            slots[4] = Marshal.GetFunctionPointerForDelegate(_dragOver);
            slots[5] = Marshal.GetFunctionPointerForDelegate(_dragLeave);
            slots[6] = Marshal.GetFunctionPointerForDelegate(_drop);
            _vtable = Marshal.AllocHGlobal(IntPtr.Size * slots.Length);
            Marshal.Copy(slots, 0, _vtable, slots.Length);
            _self = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_self, _vtable);
        }

        public IntPtr Pointer => _self;

        private int QueryInterface(IntPtr self, IntPtr riid, IntPtr ppv)
        {
            if (ppv == IntPtr.Zero)
                return unchecked((int)0x80004003);
            var iid = Marshal.PtrToStructure<Guid>(riid);
            if (iid == Native.IidUnknown || iid == Native.IidDropTarget)
            {
                Marshal.WriteIntPtr(ppv, self);
                AddRef(self);
                return 0;
            }

            Marshal.WriteIntPtr(ppv, IntPtr.Zero);
            return unchecked((int)0x80004002);
        }

        private uint AddRef(IntPtr self) => (uint)Interlocked.Increment(ref _refs);

        private uint Release(IntPtr self)
        {
            var left = Interlocked.Decrement(ref _refs);
            return (uint)Math.Max(left, 0);
        }

        private int DragEnter(IntPtr self, IntPtr dataObject, uint keyState, long pt, IntPtr effect)
        {
            WriteEffect(effect, CopyEffect);
            return 0;
        }

        private int DragOver(IntPtr self, uint keyState, long pt, IntPtr effect)
        {
            WriteEffect(effect, CopyEffect);
            return 0;
        }

        private int DragLeave(IntPtr self) => 0;

        private int Drop(IntPtr self, IntPtr dataObject, uint keyState, long pt, IntPtr effect)
        {
            IReadOnlyList<string> paths = [];
            try
            {
                paths = Native.ReadFiles(dataObject);
            }
            catch
            {
                paths = [];
            }

            WriteEffect(effect, paths.Count > 0 ? CopyEffect : 0);
            if (paths.Count > 0)
                _import(paths.ToArray());
            return 0;
        }

        private static void WriteEffect(IntPtr effect, uint value)
        {
            if (effect != IntPtr.Zero)
                Marshal.WriteInt32(effect, unchecked((int)value));
        }
    }

    private static class Native
    {
        public static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
        public static readonly Guid IidDropTarget = new("00000122-0000-0000-C000-000000000046");

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int QueryInterfaceFn(IntPtr self, IntPtr riid, IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate uint AddRefFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate uint ReleaseFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DragEnterFn(IntPtr self, IntPtr dataObject, uint keyState, long pt, IntPtr effect);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DragOverFn(IntPtr self, uint keyState, long pt, IntPtr effect);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DragLeaveFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DropFn(IntPtr self, IntPtr dataObject, uint keyState, long pt, IntPtr effect);

        public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lparam);

        [StructLayout(LayoutKind.Sequential)]
        public struct FormatEtc
        {
            public ushort cfFormat;
            public IntPtr ptd;
            public uint dwAspect;
            public int lindex;
            public uint tymed;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StgMedium
        {
            public uint tymed;
            public IntPtr union;
            public IntPtr unk;
        }

        [ComImport]
        [Guid("0000010e-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDataObject
        {
            [PreserveSig] int GetData(ref FormatEtc format, out StgMedium medium);
            [PreserveSig] int GetDataHere(ref FormatEtc format, ref StgMedium medium);
            [PreserveSig] int QueryGetData(ref FormatEtc format);
            [PreserveSig] int GetCanonicalFormatEtc(ref FormatEtc formatIn, out FormatEtc formatOut);
            [PreserveSig] int SetData(ref FormatEtc format, ref StgMedium medium, [MarshalAs(UnmanagedType.Bool)] bool release);
            [PreserveSig] int EnumFormatEtc(uint direction, out IntPtr enumerator);
            [PreserveSig] int DAdvise(ref FormatEtc format, uint advf, IntPtr sink, out uint connection);
            [PreserveSig] int DUnadvise(uint connection);
            [PreserveSig] int EnumDAdvise(out IntPtr enumerator);
        }

        public static IReadOnlyList<string> ReadFiles(IntPtr dataObject)
        {
            if (dataObject == IntPtr.Zero)
                return [];
            var data = (IDataObject)Marshal.GetObjectForIUnknown(dataObject);
            var fmt = new FormatEtc { cfFormat = 15, dwAspect = 1, lindex = -1, tymed = 1 };
            var medium = new StgMedium();
            try
            {
                if (data.GetData(ref fmt, out medium) != 0 || medium.union == IntPtr.Zero)
                    return [];
                var count = DragQueryFile(medium.union, 0xFFFFFFFF, null, 0);
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var len = (int)DragQueryFile(medium.union, i, null, 0);
                    var sb = new StringBuilder(len + 1);
                    DragQueryFile(medium.union, i, sb, (uint)sb.Capacity);
                    if (sb.Length > 0)
                        paths.Add(sb.ToString());
                }
                return paths;
            }
            finally
            {
                if (medium.union != IntPtr.Zero)
                    ReleaseStgMedium(ref medium);
                Marshal.ReleaseComObject(data);
            }
        }

        [DllImport("ole32.dll")]
        public static extern int RegisterDragDrop(IntPtr hwnd, IntPtr target);

        [DllImport("ole32.dll")]
        public static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("ole32.dll")]
        public static extern int OleInitialize(IntPtr reserved);

        [DllImport("ole32.dll")]
        public static extern void ReleaseStgMedium(ref StgMedium medium);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
        public static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint cch);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc proc, IntPtr lparam);
    }
}
