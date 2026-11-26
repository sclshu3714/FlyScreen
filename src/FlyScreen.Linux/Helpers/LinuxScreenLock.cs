using System.Diagnostics;
using FlyScreen.Linux.LockInput;

namespace FlyScreen.Linux.Helpers;

/// <summary>Linux 本机锁屏：优先 loginctl，其次 xdg-screensaver；可选随后息屏。</summary>
public static class LinuxScreenLock
{
    public static void TryLock(bool turnOffDisplay = false)
    {
        LinuxLockScreenSession.EnsureStarted();
        if (!TryRun("loginctl", "lock-session"))
            TryRun("xdg-screensaver", "lock");

        if (!turnOffDisplay) return;
        try
        {
            WaitUntilLocked(timeoutMs: 3000);
            Thread.Sleep(200);
            TryTurnOffDisplay();
        }
        catch { /* ignore */ }
    }

    public static void TryTurnOffDisplay()
    {
        if (TryRun("xset", "dpms force off")) return;
        TryRun("busctl", "--user set-property org.gnome.Mutter.DisplayConfig /org/gnome/Mutter/DisplayConfig org.gnome.Mutter.DisplayConfig PowerSaveMode i 1");
    }

    /// <summary>唤醒显示器（息屏后远程解锁前置）。</summary>
    public static void TryWakeDisplay()
    {
        if (TryRun("xset", "dpms force on")) return;
        TryRun("busctl", "--user set-property org.gnome.Mutter.DisplayConfig /org/gnome/Mutter/DisplayConfig org.gnome.Mutter.DisplayConfig PowerSaveMode i 0");
    }

    private static void WaitUntilLocked(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            try { LinuxLockScreenSession.EnsureStarted(); } catch { /* ignore */ }
            if (LinuxLockScreenSession.IsLocked) return;
            Thread.Sleep(50);
        }
    }

    private static bool TryRun(string fileName, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
