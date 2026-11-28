using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

/// <summary>
/// 在保险箱窗口上接收资源管理器拖入的文件和文件夹。
/// 主程序以管理员运行，OLE 拖放会被系统画成禁止光标。撤销 OLE 后改走 WM_DROPFILES。
/// </summary>
internal sealed class VaultWindowDrop
{
    private const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MsgfltAllow = 1;
    private static readonly IntPtr SubclassId = new(0x46535644); // 'FVSD'
    private static int _filterArmed;
    private Action<IReadOnlyList<string>>? _import;
    private Native.SubclassProc? _subclassProc;
    private readonly HashSet<nint> _subclassed = new();

    public void Attach(Window? window, Action<IReadOnlyList<string>> import)
    {
        if (!OperatingSystem.IsWindows() || window == null)
            return;
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
            return;

        _import = import;
        AllowDropMessages();
        var seen = new HashSet<nint>();
        Prepare(hwnd, seen);
        Native.EnumChildWindows(hwnd, (child, _) =>
        {
            Prepare(child, seen);
            return true;
        }, IntPtr.Zero);
    }

    public void Detach()
    {
        foreach (var hwnd in _subclassed.ToArray())
        {
            try
            {
                Native.DragAcceptFiles(hwnd, false);
                if (_subclassProc != null)
                    Native.RemoveWindowSubclass(hwnd, _subclassProc, SubclassId);
            }
            catch
            {
                // ignore
            }
        }
        _subclassed.Clear();
        _import = null;
    }

    private void Prepare(IntPtr hwnd, HashSet<nint> seen)
    {
        if (hwnd == IntPtr.Zero || !seen.Add(hwnd))
            return;
        AllowDropMessages(hwnd);
        try { Native.RevokeDragDrop(hwnd); } catch { /* 没有 OLE 目标时会失败 */ }
        Native.DragAcceptFiles(hwnd, true);
        _subclassProc ??= SubclassHook;
        if (_subclassed.Add(hwnd))
            Native.SetWindowSubclass(hwnd, _subclassProc, SubclassId, IntPtr.Zero);
    }

    private IntPtr SubclassHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WmDropFiles && wParam != IntPtr.Zero)
        {
            try
            {
                var paths = Native.ReadHDrop(wParam);
                if (paths.Count > 0)
                    _import?.Invoke(paths);
            }
            catch
            {
                // ignore
            }
            finally
            {
                Native.DragFinish(wParam);
            }
            return IntPtr.Zero;
        }

        return Native.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 主程序是管理员进程。不放行这三条消息时，资源管理器的拖放到不了窗口，光标一直是禁止。
    /// </summary>
    private static void AllowDropMessages(IntPtr hwnd = default)
    {
        if (Interlocked.Exchange(ref _filterArmed, 1) == 0)
        {
            try
            {
                Native.ChangeWindowMessageFilter(WmDropFiles, MsgfltAllow);
                Native.ChangeWindowMessageFilter(WmCopyData, MsgfltAllow);
                Native.ChangeWindowMessageFilter(WmCopyGlobalData, MsgfltAllow);
            }
            catch
            {
                Interlocked.Exchange(ref _filterArmed, 0);
            }
        }

        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            Native.ChangeWindowMessageFilterEx(hwnd, WmDropFiles, MsgfltAllow, IntPtr.Zero);
            Native.ChangeWindowMessageFilterEx(hwnd, WmCopyData, MsgfltAllow, IntPtr.Zero);
            Native.ChangeWindowMessageFilterEx(hwnd, WmCopyGlobalData, MsgfltAllow, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }
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

    private static class Native
    {
        public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lparam);

        public delegate IntPtr SubclassProc(
            IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

        public static IReadOnlyList<string> ReadHDrop(IntPtr hDrop)
        {
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var len = (int)DragQueryFile(hDrop, i, null, 0);
                var sb = new StringBuilder(len + 1);
                DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                if (sb.Length > 0)
                    paths.Add(sb.ToString());
            }
            return paths;
        }

        [DllImport("ole32.dll")]
        public static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("shell32.dll")]
        public static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(IntPtr hDrop);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
        public static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint cch);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc proc, IntPtr lparam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ChangeWindowMessageFilterEx(
            IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        public static extern bool SetWindowSubclass(
            IntPtr hwnd, SubclassProc proc, IntPtr id, IntPtr data);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        public static extern bool RemoveWindowSubclass(
            IntPtr hwnd, SubclassProc proc, IntPtr id);

        [DllImport("comctl32.dll", ExactSpelling = true)]
        public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
