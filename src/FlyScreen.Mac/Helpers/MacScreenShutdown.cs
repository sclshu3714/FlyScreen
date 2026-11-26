using System.Diagnostics;

namespace FlyScreen.Mac.Helpers;

/// <summary>macOS 本机关机。</summary>
public static class MacScreenShutdown
{
    public static void TryShutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = "-e \"tell application \\\"System Events\\\" to shut down\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // ignore
        }
    }
}
