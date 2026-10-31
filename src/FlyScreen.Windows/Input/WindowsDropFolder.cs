using System.Runtime.InteropServices;
using FlyScreen.Native;

namespace FlyScreen.Input;

/// <summary>光标下的资源管理器目录；对不上时用桌面。</summary>
public static class WindowsDropFolder
{
    public static string Resolve()
    {
        try
        {
            if (GetCursorPos(out var pt))
            {
                var hwnd = WindowFromPoint(pt);
                var folder = FolderFromWindow(hwnd);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    return folder;
            }
        }
        catch
        {
            // 解析失败时落到桌面
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : desktop;
    }

    private static string? FolderFromWindow(IntPtr hwnd)
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
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativeMethods.POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
}
