using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Native;

namespace FlyScreen.Input;

/// <summary>
/// 光标下可以放下文件的目录：资源管理器当前文件夹、其中的子文件夹，或桌面。
/// 不是这些位置时返回空，调用方取消拖放。
/// </summary>
public static class WindowsDropFolder
{
    private static readonly object Gate = new();
    private static readonly object StaGate = new();
    private static Thread? _sta;
    private static Action? _staWork;
    private static readonly object CoreGate = new();
    private static readonly AutoResetEvent StaKick = new(false);

    private static string _cached = "";
    private static long _cachedAt;
    private static int _cachedX = int.MinValue;
    private static int _cachedY = int.MinValue;

    public static string Resolve()
    {
        if (GetCursorPos(out var pt))
        {
            var now = Environment.TickCount64;
            lock (Gate)
            {
                if (now - _cachedAt < 120
                    && Math.Abs(pt.X - _cachedX) < 6
                    && Math.Abs(pt.Y - _cachedY) < 6)
                    return _cached;
            }
        }

        var value = OnSta(ResolveCore);
        if (GetCursorPos(out pt))
        {
            lock (Gate)
            {
                _cached = value;
                _cachedAt = Environment.TickCount64;
                _cachedX = pt.X;
                _cachedY = pt.Y;
            }
        }

        return value;
    }

    private static string OnSta(Func<string> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return work();

        EnsureSta();
        string result = "";
        using var done = new ManualResetEventSlim(false);
        lock (StaGate)
        {
            _staWork = () =>
            {
                try { result = work(); }
                catch { result = ""; }
                finally { done.Set(); }
            };
            StaKick.Set();
        }

        done.Wait(TimeSpan.FromSeconds(2));
        return result;
    }

    private static void EnsureSta()
    {
        lock (StaGate)
        {
            if (_sta != null) return;
            _sta = new Thread(StaLoop) { IsBackground = true, Name = "FlyScreen.DropFolder" };
            _sta.SetApartmentState(ApartmentState.STA);
            _sta.Start();
        }
    }

    private static void StaLoop()
    {
        while (true)
        {
            StaKick.WaitOne();
            Action? work;
            lock (StaGate)
            {
                work = _staWork;
                _staWork = null;
            }

            try { work?.Invoke(); }
            catch { /* 调用方已超时 */ }
        }
    }

    private static string ResolveCore()
    {
        lock (CoreGate)
        try
        {
            if (!GetCursorPos(out var pt)) return "";
            var hwnd = HitWindow(pt);
            if (hwnd == IntPtr.Zero) return "";
            var container = ContainerOf(hwnd);
            if (string.IsNullOrWhiteSpace(container) || !Directory.Exists(container))
                return "";
            var into = ItemDirectory(pt, container);
            return string.IsNullOrWhiteSpace(into) ? "" : into;
        }
        catch
        {
            return "";
        }
    }

    private static IntPtr HitWindow(NativeMethods.POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (!IsOwnWindow(hwnd)) return hwnd;
        ShowWindow(hwnd, 0);
        var under = WindowFromPoint(pt);
        ShowWindow(hwnd, 8);
        return under;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        var buf = new StringBuilder(64);
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
        {
            buf.Clear();
            if (GetClassName(h, buf, buf.Capacity) == 0) continue;
            var name = buf.ToString();
            if (name is "FlyScreenEdgeDragCue" or "FlyScreenEdgeDrag")
                return true;
        }

        return false;
    }

    private static string? ContainerOf(IntPtr hwnd)
    {
        var desktop = false;
        var explorer = false;
        var buf = new StringBuilder(64);
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
        {
            buf.Clear();
            if (GetClassName(h, buf, buf.Capacity) == 0) continue;
            switch (buf.ToString())
            {
                case "CabinetWClass":
                case "ExploreWClass":
                    explorer = true;
                    break;
                case "Progman":
                case "WorkerW":
                case "SHELLDLL_DefView":
                    desktop = true;
                    break;
            }
        }

        if (explorer)
        {
            var path = FolderFromShellWindow(hwnd);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && !path.StartsWith("::", StringComparison.Ordinal))
                return path;
            return null;
        }

        if (desktop)
            return DesktopPath();
        return null;
    }

    private static string DesktopPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : desktop;
    }

    private static string? FolderFromShellWindow(IntPtr hwnd)
    {
        var wanted = new HashSet<long>();
        for (var h = hwnd; h != IntPtr.Zero; h = GetParent(h))
            wanted.Add(h.ToInt64());

        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return null;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell == null) return null;
        try
        {
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    long id = Convert.ToInt64(window.HWND);
                    if (!wanted.Contains(id)) continue;
                    string path = window.Document.Folder.Self.Path;
                    if (!string.IsNullOrWhiteSpace(path))
                        return path;
                }
                catch
                {
                    // 单个窗口读不到就跳过
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>光标压在子文件夹上时进入该文件夹；压在特殊图标上则不能放。</summary>
    private static string ItemDirectory(NativeMethods.POINT pt, string container)
    {
        if (AccessibleObjectFromPoint(pt, out var acc, out var child) != 0 || acc == null)
            return container;

        try
        {
            if (ChildId(child) == 0)
                return container;

            var name = FirstLine(AccProp(acc, "accName", child));
            if (IsBlockedName(name))
                return "";

            var value = FirstLine(AccProp(acc, "accValue", child));
            if (IsDirectory(value))
                return value!;

            if (!string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                var combined = Path.Combine(container, name);
                if (IsDirectory(combined))
                    return combined;
            }

            return container;
        }
        catch
        {
            return container;
        }
        finally
        {
            try { Marshal.ReleaseComObject(acc); } catch { /* ignore */ }
        }
    }

    private static int ChildId(object? child) => child switch
    {
        int i => i,
        short s => s,
        long l => (int)l,
        _ => -1
    };

    private static bool IsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("::", StringComparison.Ordinal))
            return false;
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static bool IsBlockedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Trim() is "回收站" or "Recycle Bin" or "此电脑" or "This PC" or "Computer"
            or "控制面板" or "Control Panel";
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Split('\r', '\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }

    private static string? AccProp(object acc, string property, object? child)
    {
        try
        {
            var result = acc.GetType().InvokeMember(
                property,
                System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null,
                acc,
                [child ?? 0]);
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativeMethods.POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(
        NativeMethods.POINT pt,
        [MarshalAs(UnmanagedType.IUnknown)] out object? acc,
        out object? child);
}
