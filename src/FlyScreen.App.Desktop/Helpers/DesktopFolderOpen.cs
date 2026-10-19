using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// 跨平台打开目录。Linux 上不能对文件夹路径 UseShellExecute（常直接失败）；
/// sudo 启动时需降权到 SUDO_USER 并带上 DISPLAY/DBUS，文件管理器才能弹出。
/// </summary>
internal static class DesktopFolderOpen
{
    public static bool TryOpen(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TryStart(directory, useShellExecute: true, out error);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TryStartProcess("open", [directory], out error);

        // —— Linux / WSL ——
        if (TryOpenViaWslExplorer(directory, out error))
            return true;

        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        var asRoot = string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(Environment.GetEnvironmentVariable("USER"), "root", StringComparison.OrdinalIgnoreCase);

        if (asRoot && !string.IsNullOrWhiteSpace(sudoUser) && sudoUser != "root")
        {
            if (TryOpenAsUser(sudoUser!, directory, out error))
                return true;
        }

        if (TryStartProcess("xdg-open", [directory], out error))
            return true;
        if (TryStartProcess("gio", ["open", directory], out error))
            return true;

        error ??= "无法打开文件管理器（请确认已安装 xdg-open）";
        return false;
    }

    private static bool TryOpenViaWslExplorer(string directory, out string? error)
    {
        error = null;
        if (!Directory.Exists("/mnt/wslg") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
            return false;

        try
        {
            using var map = Process.Start(new ProcessStartInfo
            {
                FileName = "wslpath",
                ArgumentList = { "-w", directory },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (map == null) return false;
            var winPath = map.StandardOutput.ReadToEnd().Trim();
            map.WaitForExit(3000);
            if (map.ExitCode != 0 || string.IsNullOrWhiteSpace(winPath))
                return false;

            using var exp = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { winPath },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            // explorer 常返回非 0，但窗口已开
            return exp != null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryOpenAsUser(string user, string directory, out string? error)
    {
        error = null;
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var xauth = Environment.GetEnvironmentVariable("XAUTHORITY");
        if (string.IsNullOrWhiteSpace(xauth))
        {
            var home = Path.Combine("/home", user);
            var candidate = Path.Combine(home, ".Xauthority");
            if (File.Exists(candidate))
                xauth = candidate;
        }

        var uid = TryResolveUid(user);
        var dbus = uid >= 0 ? $"unix:path=/run/user/{uid}/bus" : null;

        var envArgs = new List<string>
        {
            $"DISPLAY={display}"
        };
        if (!string.IsNullOrWhiteSpace(xauth))
            envArgs.Add($"XAUTHORITY={xauth}");
        if (!string.IsNullOrWhiteSpace(dbus))
            envArgs.Add($"DBUS_SESSION_BUS_ADDRESS={dbus}");
        envArgs.Add("xdg-open");
        envArgs.Add(directory);

        // runuser（util-linux）优先；失败再试 sudo -n -u
        if (TryStartProcess("runuser", Prepend(["-u", user, "--", "env"], envArgs), out error))
            return true;
        if (TryStartProcess("sudo", Prepend(["-n", "-u", user, "env"], envArgs), out error))
            return true;
        return false;
    }

    private static string[] Prepend(string[] head, List<string> tail)
    {
        var all = new string[head.Length + tail.Count];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        return all;
    }

    private static int TryResolveUid(string user)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "id",
                ArgumentList = { "-u", user },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (p == null) return -1;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return int.TryParse(s, out var uid) ? uid : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryStartProcess(string fileName, IReadOnlyList<string> args, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null)
            {
                error = $"无法启动 {fileName}";
                return false;
            }

            if (!p.WaitForExit(4000))
                return true; // 仍在跑，多半已拉起文件管理器

            if (p.ExitCode == 0)
                return true;

            var err = p.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(err))
                err = p.StandardOutput.ReadToEnd().Trim();
            error = string.IsNullOrEmpty(err) ? $"{fileName} 退出码 {p.ExitCode}" : err;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryStart(string fileName, bool useShellExecute, out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = useShellExecute
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
