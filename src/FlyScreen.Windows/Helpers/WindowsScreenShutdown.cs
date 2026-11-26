using System.Diagnostics;

namespace FlyScreen.Helpers;

/// <summary>Windows 本机关机。</summary>
public static class WindowsScreenShutdown
{
    public static void TryShutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/s /t 0 /f",
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
