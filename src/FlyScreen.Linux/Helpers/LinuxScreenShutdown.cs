using System.Diagnostics;

namespace FlyScreen.Linux.Helpers;

/// <summary>Linux 本机关机。</summary>
public static class LinuxScreenShutdown
{
    public static void TryShutdown()
    {
        if (TryRun("systemctl", "poweroff")) return;
        TryRun("shutdown", "-h now");
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
                CreateNoWindow = true
            });
            return p != null;
        }
        catch
        {
            return false;
        }
    }
}
