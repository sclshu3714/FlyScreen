using System.Diagnostics;

namespace FlyScreen.Linux.Helpers;

/// <summary>Linux 本机锁屏：优先 loginctl，其次 xdg-screensaver。</summary>
public static class LinuxScreenLock
{
    public static void TryLock()
    {
        if (TryRun("loginctl", "lock-session")) return;
        TryRun("xdg-screensaver", "lock");
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
