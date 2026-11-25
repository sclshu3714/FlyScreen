using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FlyScreen.Linux.Input;

/// <summary>
/// 穿边拖放下目录：按桌面环境解析当前文件管理器窗口，失败则桌面。
/// </summary>
public static class LinuxDropFolder
{
    public static string Resolve()
    {
        foreach (var candidate in new Func<string?>[]
                 {
                     TryNautilusLocations,
                     TryDolphinCurrentUrl,
                     TryThunarViaXdotool,
                     TryXdgDesktop
                 })
        {
            try
            {
                var path = candidate();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    return path!;
            }
            catch
            {
                // try next
            }
        }

        return DesktopDirectory();
    }

    private static string? TryNautilusLocations()
    {
        var uri = TryRun(
            "gdbus",
            "call --session --dest org.gnome.Nautilus --object-path /org/gnome/Nautilus " +
            "--method org.gnome.Nautilus.FileManager1.GetLocations");
        return TryUriToPath(uri);
    }

    private static string? TryDolphinCurrentUrl()
    {
        // Dolphin DBus：org.freedesktop.FileManager1 或 org.kde.dolphin
        var raw = TryRun(
            "qdbus",
            "org.kde.dolphin-* /dolphin/Dolphin_1 org.kde.dolphin.MainWindow.currentUrl");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = TryRun(
                "dbus-send",
                "--session --print-reply --dest=org.freedesktop.FileManager1 " +
                "/org/freedesktop/FileManager1 org.freedesktop.DBus.Properties.Get " +
                "string:org.freedesktop.FileManager1 string:OpenDirectories");
        }

        return TryUriToPath(raw);
    }

    private static string? TryThunarViaXdotool()
    {
        // 活动窗口标题常含路径（部分主题）；再退回 xprop WM_CLASS=Thunar 时用桌面
        var focus = TryRun("xdotool", "getactivewindow getwindowname")?.Trim();
        if (string.IsNullOrWhiteSpace(focus)) return null;

        // 形如 "Documents — File Manager" / "/home/u/Docs"
        var m = Regex.Match(focus, @"(/[^\s—–|]+)");
        if (m.Success)
        {
            var p = m.Groups[1].Value.TrimEnd('/', '\\');
            if (Directory.Exists(p)) return p;
        }

        return null;
    }

    private static string? TryXdgDesktop()
    {
        var desk = TryRun("xdg-user-dir", "DESKTOP")?.Trim();
        return !string.IsNullOrWhiteSpace(desk) && Directory.Exists(desk) ? desk : null;
    }

    private static string? TryUriToPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var idx = raw.IndexOf("file://", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var end = raw.IndexOfAny(['\'', '"', ',', ')', ' ', '\n', '\r'], idx + 7);
        var uri = end > idx ? raw[idx..end] : raw[idx..];
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
            {
                var local = Uri.UnescapeDataString(u.LocalPath);
                if (Directory.Exists(local)) return local;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? TryRun(string fileName, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return null;
        var stdout = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(1200))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return null;
        }

        return proc.ExitCode == 0 ? stdout : null;
    }

    private static string DesktopDirectory()
    {
        var dir = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        var home = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.UserProfile);
        var desk = Path.Combine(home, "Desktop");
        return Directory.Exists(desk) ? desk : home;
    }
}
