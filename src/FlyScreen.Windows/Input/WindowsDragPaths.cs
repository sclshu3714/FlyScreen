using System.Runtime.InteropServices;
using System.Text;

namespace FlyScreen.Input;

/// <summary>
/// 穿边时要带走的文件。本程序以管理员运行，资源管理器的拖放进不了接收条，
/// 所以在左键按下后读取当前窗口里已经选中的文件和文件夹。
/// </summary>
public static class WindowsDragPaths
{
    private static readonly object Gate = new();
    private static string[] _snapshot = [];
    private static int _reading;
    private static int _generation;

    public static void NoteButtonDown()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1) return;
        var generation = Interlocked.Increment(ref _generation);
        var foreground = GetForegroundWindow();
        var thread = new Thread(() =>
        {
            try
            {
                // 等资源管理器先处理按下（选中该项），又赶在拖放循环开始前读完
                Thread.Sleep(40);
                if (generation != Volatile.Read(ref _generation)) return;
                if (!IsShellFolder(foreground))
                {
                    lock (Gate) _snapshot = [];
                    return;
                }

                if (TryReadShell(foreground, out var paths) && generation == Volatile.Read(ref _generation))
                    lock (Gate) _snapshot = paths;
            }
            catch
            {
                if (generation == Volatile.Read(ref _generation))
                    lock (Gate) _snapshot = [];
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        })
        {
            IsBackground = true,
            Name = "FlyScreen.DragPaths"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void NoteButtonUp()
    {
        Interlocked.Increment(ref _generation);
        lock (Gate) _snapshot = [];
    }

    public static bool TryGet(out string[] paths)
    {
        if (EdgeFileDragCapture.TryCopyPaths(out paths) && paths.Length > 0)
            return true;

        // 管理员进程里，资源管理器拖放光标经常对不上系统句柄。
        // 左键仍按着、且按下时已记下选中项，就视为在拖文件；否则鼠标能穿边，文件却带不过去。
        lock (Gate)
        {
            if (_snapshot.Length == 0)
            {
                paths = [];
                return false;
            }

            paths = _snapshot.ToArray();
            return true;
        }
    }

    private static bool IsShellFolder(IntPtr hwnd)
    {
        var buf = new StringBuilder(64);
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
        {
            buf.Clear();
            if (GetClassName(h, buf, buf.Capacity) == 0) continue;
            var name = buf.ToString();
            if (name is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW" or "SHELLDLL_DefView")
                return true;
        }

        return false;
    }

    private static bool TryReadShell(IntPtr foreground, out string[] paths)
    {
        paths = [];
        var wanted = new HashSet<long>();
        for (var h = foreground; h != IntPtr.Zero; h = GetParent(h))
            wanted.Add(h.ToInt64());
        var desktop = IsDesktop(foreground);

        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return false;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell == null) return false;

        var list = new List<string>();
        try
        {
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    long id = Convert.ToInt64(window.HWND);
                    var match = wanted.Contains(id) || (desktop && id == 0);
                    if (!match) continue;
                    dynamic items = window.Document.SelectedItems();
                    int count = items.Count;
                    for (var i = 0; i < count && list.Count < 500; i++)
                    {
                        string path = items.Item(i).Path;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (path.StartsWith("::", StringComparison.Ordinal)) continue;
                        if (!File.Exists(path) && !Directory.Exists(path)) continue;
                        if (list.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                        list.Add(path);
                    }

                    if (list.Count > 0) break;
                }
                catch
                {
                    // 单个窗口不是文件夹就跳过
                }
            }
        }
        catch
        {
            return false;
        }

        if (list.Count == 0) return false;
        paths = list.ToArray();
        return true;
    }

    private static bool IsDesktop(IntPtr hwnd)
    {
        var buf = new StringBuilder(64);
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
        {
            buf.Clear();
            if (GetClassName(h, buf, buf.Capacity) == 0) continue;
            var name = buf.ToString();
            if (name is "Progman" or "WorkerW" or "SHELLDLL_DefView")
                return true;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
}
