using System.Diagnostics;
using FlyScreen.Linux.Environment;

namespace FlyScreen.Linux.Helpers;

/// <summary>
/// Linux 空闲抑制：优先 Session ScreenSaver D-Bus Inhibit；失败则周期性 xdg-screensaver reset。
/// </summary>
public static class LinuxIdleInhibit
{
    private static readonly object Gate = new();
    private static bool _active;
    private static uint _cookie;
    private static string? _inhibitDest;
    private static Timer? _timer;

    public static void SetActive(bool active)
    {
        if (!LinuxRuntimeEnvironment.IsLinux)
            return;

        lock (Gate)
        {
            if (active)
            {
                _active = true;
                if (_cookie == 0)
                    TryInhibit();
                _timer ??= new Timer(_ => Pulse(), null, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40));
            }
            else
            {
                _active = false;
                try { _timer?.Dispose(); } catch { /* ignore */ }
                _timer = null;
                TryUninhibit();
            }
        }
    }

    private static void Pulse()
    {
        lock (Gate)
        {
            if (!_active) return;
            if (_cookie == 0)
                TryInhibit();
            if (_cookie == 0)
                TryRun("xdg-screensaver", "reset");
        }
    }

    private static void TryInhibit()
    {
        // GNOME / 兼容实现
        foreach (var dest in new[] { "org.freedesktop.ScreenSaver", "org.gnome.ScreenSaver" })
        {
            var reply = TryRun(
                "dbus-send",
                $"--session --print-reply --dest={dest} /{dest.Replace('.', '/')} " +
                $"{dest}.Inhibit string:FlyScreen string:block-screensaver");
            if (TryParseCookie(reply, out var cookie))
            {
                _cookie = cookie;
                _inhibitDest = dest;
                return;
            }
        }
    }

    private static void TryUninhibit()
    {
        if (_cookie == 0 || string.IsNullOrEmpty(_inhibitDest))
        {
            _cookie = 0;
            _inhibitDest = null;
            return;
        }

        var dest = _inhibitDest;
        var path = "/" + dest.Replace('.', '/');
        TryRun(
            "dbus-send",
            $"--session --print-reply --dest={dest} {path} {dest}.UnInhibit uint32:{_cookie}");
        _cookie = 0;
        _inhibitDest = null;
    }

    private static bool TryParseCookie(string? reply, out uint cookie)
    {
        cookie = 0;
        if (string.IsNullOrWhiteSpace(reply)) return false;
        // dbus-send 典型：uint32 12345
        var idx = reply.IndexOf("uint32", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var span = reply.AsSpan(idx + 6).TrimStart();
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end])) end++;
        return end > 0 && uint.TryParse(span[..end], out cookie) && cookie != 0;
    }

    private static string? TryRun(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}
