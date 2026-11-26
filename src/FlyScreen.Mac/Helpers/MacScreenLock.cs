using System.Diagnostics;

namespace FlyScreen.Mac.Helpers;

/// <summary>macOS 本机锁屏（CGSession -suspend）；可选随后息屏。</summary>
public static class MacScreenLock
{
    public static void TryLock(bool turnOffDisplay = false)
    {
        try
        {
            var cg = "/System/Library/CoreServices/Menu Extras/User.menu/Contents/Resources/CGSession";
            if (File.Exists(cg))
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = cg,
                    Arguments = "-suspend",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = "-e \"tell application \\\"System Events\\\" to keystroke \\\"q\\\" using {control down, command down}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }
        catch { /* ignore */ }

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
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/pmset",
                Arguments = "displaysleepnow",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { /* ignore */ }
    }
}
