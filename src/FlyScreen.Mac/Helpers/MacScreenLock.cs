using System.Diagnostics;

namespace FlyScreen.Mac.Helpers;

/// <summary>macOS 本机锁屏（CGSession -suspend）。</summary>
public static class MacScreenLock
{
    public static void TryLock()
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
                return;
            }
        }
        catch { /* fallback */ }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                Arguments = "-e \"tell application \\\"System Events\\\" to keystroke \\\"q\\\" using {control down, command down}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { /* ignore */ }
    }
}
