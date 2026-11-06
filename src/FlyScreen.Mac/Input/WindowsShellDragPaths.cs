using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

#pragma warning disable CA1416 // 仅 Windows 穿屏拖文件调用，入口已判断操作系统

namespace FlyScreen.Mac.Input;

/// <summary>
/// Windows 上穿边拖文件：靠近屏幕外缘时读资源管理器当前选中路径。
/// 调试器会把短命 STA 线程显示成已退出，所以读路径的线程一直活着。
/// </summary>
internal static class WindowsShellDragPaths
{
    private const int ShellReadDelayMs = 160;

    private static readonly object Gate = new();
    private static readonly object StaGate = new();
    private static readonly ConcurrentQueue<Action> StaJobs = new();
    private static readonly AutoResetEvent StaKick = new(false);
    private static string[] _snapshot = [];
    private static int _generation;
    private static int _prefetchQueued;
    private static long _buttonDownTick;
    private static Thread? _sta;

    public static void NoteButtonDown()
        => Volatile.Write(ref _buttonDownTick, Environment.TickCount64);

    public static void NoteButtonUp()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _prefetchQueued, 0);
        lock (Gate) _snapshot = [];
    }

    public static void PrefetchIfNeeded()
    {
        lock (Gate)
        {
            if (_snapshot.Length > 0) return;
        }

        if (Interlocked.CompareExchange(ref _prefetchQueued, 1, 0) != 0)
            return;

        var generation = Volatile.Read(ref _generation);
        var foreground = GetForegroundWindow();
        var downTick = Volatile.Read(ref _buttonDownTick);
        if (downTick == 0)
            downTick = Environment.TickCount64;

        PostSta(() =>
        {
            try
            {
                var wait = (int)(ShellReadDelayMs - (Environment.TickCount64 - downTick));
                if (wait is > 0 and < 500)
                    Thread.Sleep(wait);
                if (generation != Volatile.Read(ref _generation))
                    return;
                if (!IsShellFolder(foreground))
                    return;
                if (TryReadShell(foreground, out var paths)
                    && generation == Volatile.Read(ref _generation)
                    && paths.Length > 0)
                {
                    lock (Gate) _snapshot = paths;
                }
            }
            catch
            {
                // 穿边时 TryGet 再扫一次
            }
            finally
            {
                Interlocked.Exchange(ref _prefetchQueued, 0);
            }
        });
    }

    public static bool TryGet(out string[] paths)
    {
        lock (Gate)
        {
            if (_snapshot.Length > 0)
            {
                paths = _snapshot.ToArray();
                return true;
            }
        }

        var downTick = Volatile.Read(ref _buttonDownTick);
        if (downTick != 0 && Environment.TickCount64 - downTick < ShellReadDelayMs)
        {
            paths = [];
            return false;
        }

        if (OnSta(TryReadAnyShellSelection, out paths) && paths.Length > 0)
        {
            lock (Gate) _snapshot = paths;
            return true;
        }

        paths = [];
        return false;
    }

    private static void PostSta(Action work)
    {
        EnsureSta();
        StaJobs.Enqueue(work);
        StaKick.Set();
    }

    private static bool OnSta(Func<string[]> work, out string[] paths)
    {
        paths = [];
        string[] result = [];
        using var done = new ManualResetEventSlim(false);
        PostSta(() =>
        {
            try { result = work() ?? []; }
            catch { result = []; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromMilliseconds(400)))
            return false;
        paths = result;
        return paths.Length > 0;
    }

    private static void EnsureSta()
    {
        if (Volatile.Read(ref _sta) != null) return;
        lock (StaGate)
        {
            if (_sta != null) return;
            _sta = new Thread(StaLoop)
            {
                IsBackground = true,
                Name = "FlyScreen.EdgeDragPaths"
            };
            _sta.SetApartmentState(ApartmentState.STA);
            _sta.Start();
        }
    }

    private static void StaLoop()
    {
        while (true)
        {
            StaKick.WaitOne();
            while (StaJobs.TryDequeue(out var work))
            {
                try { work(); }
                catch { /* 预读失败 */ }
            }
        }
    }

    private static string[] TryReadAnyShellSelection()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return [];
        dynamic? shell = Activator.CreateInstance(type);
        if (shell == null) return [];
        var list = new List<string>();
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                AppendSelected(window.Document.SelectedItems(), list);
                if (list.Count > 0) break;
            }
            catch
            {
                // 不是文件夹窗口
            }
        }

        return list.Count == 0 ? [] : list.ToArray();
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
                    if (!wanted.Contains(id) && !(desktop && id == 0)) continue;
                    AppendSelected(window.Document.SelectedItems(), list);
                    if (list.Count > 0) break;
                }
                catch
                {
                    // 单个窗口不是文件夹
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

    private static void AppendSelected(dynamic items, List<string> list)
    {
        int count = items.Count;
        for (var i = 0; i < count && list.Count < 500; i++)
        {
            string path = items.Item(i).Path;
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("::", StringComparison.Ordinal))
                continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (list.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(path);
        }
    }

    private static bool IsShellFolder(IntPtr hwnd)
    {
        var buf = new StringBuilder(64);
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
        {
            buf.Clear();
            if (GetClassName(h, buf, buf.Capacity) == 0) continue;
            if (buf.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW" or "SHELLDLL_DefView")
                return true;
        }

        return false;
    }

    private static bool IsDesktop(IntPtr hwnd)
    {
        var buf = new StringBuilder(64);
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
        {
            buf.Clear();
            if (GetClassName(h, buf, buf.Capacity) == 0) continue;
            if (buf.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView")
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
