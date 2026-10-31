using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Native;

namespace FlyScreen.Input;

/// <summary>
/// 按住左键时在虚拟屏幕外缘放一条接收区，读出正在拖的文件和文件夹。
/// </summary>
public static class EdgeFileDragCapture
{
    private const int WmArm = 0x8000 + 41;
    private const int WmDisarm = 0x8000 + 42;
    private const int Strip = 12;

    private static readonly object Gate = new();
    private static readonly WndProcDelegate Proc = WndProc;
    private static string[] _paths = [];
    private static Thread? _thread;
    private static IntPtr _signal;
    private static DropSink? _sink;

    public static bool HasPaths
    {
        get { lock (Gate) return _paths.Length > 0; }
    }

    public static void Arm()
    {
        EnsureThread();
        if (_signal != IntPtr.Zero)
            PostMessage(_signal, WmArm, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Disarm()
    {
        if (_signal != IntPtr.Zero)
            PostMessage(_signal, WmDisarm, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool TryCopyPaths(out string[] paths)
    {
        lock (Gate)
        {
            if (_paths.Length == 0)
            {
                paths = [];
                return false;
            }

            paths = _paths.ToArray();
            return true;
        }
    }

    public static void DismissLocalDrag()
    {
        var down = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT { wVk = 0x1B, dwExtraInfo = new IntPtr(0xF15C) }
            }
        };
        var up = down;
        up.U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
        NativeMethods.SendInput(1, [down], Marshal.SizeOf<NativeMethods.INPUT>());
        NativeMethods.SendInput(1, [up], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void Remember(string[] paths)
    {
        lock (Gate) _paths = paths;
    }

    private static void EnsureThread()
    {
        lock (Gate)
        {
            if (_thread != null) return;
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "FlyScreen.EdgeDrag" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private static void ThreadMain()
    {
        OleInitialize(IntPtr.Zero);
        _sink = new DropSink();
        var hInstance = NativeMethods.GetModuleHandle(null);
        var cls = new WndClass
        {
            lpfnWndProc = Proc,
            hInstance = hInstance,
            lpszClassName = "FlyScreenEdgeDrag"
        };
        RegisterClass(ref cls);

        var hwnds = new IntPtr[4];
        for (var i = 0; i < hwnds.Length; i++)
        {
            hwnds[i] = CreateWindowEx(
                0x00000080 | 0x08000000 | 0x00080000,
                cls.lpszClassName, "",
                unchecked((int)0x80000000),
                0, 0, Strip, Strip,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnds[i] != IntPtr.Zero)
            {
                SetLayeredWindowAttributes(hwnds[i], 0, 1, 2);
                RegisterDragDrop(hwnds[i], _sink);
            }
        }

        _signal = hwnds.FirstOrDefault(h => h != IntPtr.Zero);
        var msg = new Msg();
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmArm)
                Place(hwnds, show: true);
            else if (msg.message == WmDisarm)
            {
                lock (Gate) _paths = [];
                Place(hwnds, show: false);
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void Place(IntPtr[] hwnds, bool show)
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var w = Math.Max(Strip, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var h = Math.Max(Strip, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        Move(hwnds, 0, x, y, Strip, h, show);
        Move(hwnds, 1, x + w - Strip, y, Strip, h, show);
        Move(hwnds, 2, x, y, w, Strip, show);
        Move(hwnds, 3, x, y + h - Strip, w, Strip, show);
    }

    private static void Move(IntPtr[] hwnds, int index, int x, int y, int w, int h, bool show)
    {
        if ((uint)index >= (uint)hwnds.Length || hwnds[index] == IntPtr.Zero) return;
        SetWindowPos(hwnds[index], new IntPtr(-1), x, y, w, h, 0x0010 | 0x0004);
        ShowWindow(hwnds[index], show ? 8 : 0);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hwnd, msg, wParam, lParam);

    private sealed class DropSink : IDropTarget
    {
        public int DragEnter(IntPtr data, uint keyState, long pt, ref uint effect)
        {
            var paths = ReadFiles(data);
            if (paths.Length == 0)
            {
                effect = 0;
                return 0;
            }

            Remember(paths);
            effect = 1;
            return 0;
        }

        public int DragOver(uint keyState, long pt, ref uint effect)
        {
            effect = HasPaths ? 1u : 0u;
            return 0;
        }

        public int DragLeave() => 0;

        public int Drop(IntPtr data, uint keyState, long pt, ref uint effect)
        {
            var paths = ReadFiles(data);
            if (paths.Length > 0) Remember(paths);
            effect = 0;
            return 0;
        }

        private static string[] ReadFiles(IntPtr data)
        {
            if (data == IntPtr.Zero) return [];
            var obj = (IDataObject)Marshal.GetObjectForIUnknown(data);
            var format = new FormatEtc
            {
                cfFormat = 15,
                dwAspect = 1,
                lindex = -1,
                tymed = 1
            };
            if (obj.GetData(ref format, out var medium) != 0 || medium.union == IntPtr.Zero)
                return [];
            try
            {
                var count = DragQueryFile(medium.union, 0xFFFFFFFF, null, 0);
                var list = new List<string>();
                var buf = new StringBuilder(1024);
                for (uint i = 0; i < count; i++)
                {
                    buf.Clear();
                    if (DragQueryFile(medium.union, i, buf, (uint)buf.Capacity) == 0) continue;
                    var path = buf.ToString();
                    if (File.Exists(path) || Directory.Exists(path))
                        list.Add(path);
                }

                return list.ToArray();
            }
            finally
            {
                ReleaseStgMedium(ref medium);
                Marshal.ReleaseComObject(obj);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        public uint tymed;
        public IntPtr union;
        public IntPtr unk;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(IntPtr pDataObj, uint grfKeyState, long pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, long pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IntPtr pDataObj, uint grfKeyState, long pt, ref uint pdwEffect);
    }

    [ComImport]
    [Guid("0000010e-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataObject
    {
        [PreserveSig] int GetData(ref FormatEtc pformatetcIn, out StgMedium medium);
        [PreserveSig] int GetDataHere(ref FormatEtc pformatetc, ref StgMedium medium);
        [PreserveSig] int QueryGetData(ref FormatEtc pformatetc);
        [PreserveSig] int GetCanonicalFormatEtc(ref FormatEtc a, out FormatEtc b);
        [PreserveSig] int SetData(ref FormatEtc a, ref StgMedium b, bool release);
        [PreserveSig] int EnumFormatEtc(uint direction, out IntPtr enumerator);
        [PreserveSig] int DAdvise(ref FormatEtc a, uint advf, IntPtr sink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr enumerator);
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr pvReserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref StgMedium medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? name, uint cch);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
}
