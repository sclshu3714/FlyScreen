using System.Diagnostics;

namespace FlyScreen.Mac.Input;

/// <summary>
/// 穿边拖放下目录：优先 Finder 最前窗口目标文件夹，否则桌面。
/// </summary>
public static class MacDropFolder
{
    public static string Resolve()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(
                "tell application \"Finder\" to if (count of Finder windows) > 0 then " +
                "POSIX path of (target of front Finder window as alias)");

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                if (!proc.WaitForExit(1500))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
                else if (Directory.Exists(stdout))
                    return stdout;
            }
        }
        catch
        {
            // fall through
        }

        return DesktopDirectory();
    }

    private static string DesktopDirectory()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desk = Path.Combine(home, "Desktop");
        return Directory.Exists(desk) ? desk : home;
    }
}
