using System.Diagnostics;
using FlyScreen.Mac.LockInput;

namespace FlyScreen.Mac.Helpers;

/// <summary>macOS 本机锁屏（CGSession -suspend）；可选随后息屏。</summary>
public static class MacScreenLock
{
    public static void TryLock(bool turnOffDisplay = false)
    {
        MacLockScreenSession.EnsureStarted();
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
            WaitUntilLocked(timeoutMs: 3000);
            Thread.Sleep(200);
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

    /// <summary>唤醒显示器（息屏后远程解锁前置）。</summary>
    public static void TryWakeDisplay()
    {
        try
        {
            // caffeinate -u 模拟用户活动以唤醒显示
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/caffeinate",
                Arguments = "-u -t 2",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { /* ignore */ }
    }

    private static void WaitUntilLocked(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            try { MacLockScreenSession.EnsureStarted(); } catch { /* ignore */ }
            if (MacLockScreenSession.IsLocked) return;
            Thread.Sleep(50);
        }
    }
}
