using System.Diagnostics;

namespace FlyScreen.Linux.Helpers;

/// <summary>Linux 本机锁屏：优先 loginctl，其次 xdg-screensaver；可选随后息屏。</summary>
public static class LinuxScreenLock
{
    public static void TryLock(bool turnOffDisplay = false)
    {
        if (!TryRun("loginctl", "lock-session"))
            TryRun("xdg-screensaver", "lock");

        if (!turnOffDisplay) return;
        try
        {
            Thread.Sleep(350);
            TryTurnOffDisplay();
        }
        catch { /* ignore */ }
    }

    public static void TryTurnOffDisplay()
    {
        if (TryRun("xset", "dpms force off")) return;
        TryRun("busctl", "--user set-property org.gnome.Mutter.DisplayConfig /org/gnome/Mutter/DisplayConfig org.gnome.Mutter.DisplayConfig PowerSaveMode i 1");
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
