using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// 跨平台打开目录。Linux 上不能对文件夹路径 UseShellExecute；
/// WSL 下优先用 Windows explorer.exe；sudo 时再降权到 SUDO_USER。
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

        var errors = new List<string>();

        // WSL：用资源管理器最稳（root 下 gio/xdg-open 常无文件管理器关联）
        if (IsWsl())
        {
            if (TryOpenViaWslExplorer(directory, out var wslErr))
                return true;
            if (!string.IsNullOrWhiteSpace(wslErr))
                errors.Add(wslErr);
        }

        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        var asRoot = string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(Environment.GetEnvironmentVariable("USER"), "root", StringComparison.OrdinalIgnoreCase);

        if (asRoot && !string.IsNullOrWhiteSpace(sudoUser) && sudoUser != "root")
        {
            if (TryOpenAsUser(sudoUser!, directory, out var userErr))
                return true;
            if (!string.IsNullOrWhiteSpace(userErr))
                errors.Add(userErr);
        }

        // 本机桌面：常见文件管理器（避免仅依赖 gio 的 MIME 关联）
        foreach (var (bin, args) in new (string, string[])[]
                 {
                     ("xdg-open", [directory]),
                     ("nautilus", [directory]),
                     ("dolphin", [directory]),
                     ("thunar", [directory]),
                     ("pcmanfm", [directory]),
                     ("gio", ["open", directory]),
                 })
        {
            if (TryStartProcess(bin, args, out var e))
                return true;
            if (!string.IsNullOrWhiteSpace(e) && !errors.Contains(e))
                errors.Add(e);
        }

        // 最后再试一次 WSL explorer（有的环境 /mnt/wslg 不存在但仍是 WSL）
        if (!IsWsl() && TryOpenViaWslExplorer(directory, out var lastErr))
            return true;

        error = errors.Count > 0
            ? string.Join("；", errors.Take(2))
            : "无法打开文件管理器";
        if (asRoot)
            error += "（建议用普通用户启动：./FlyScreen --gui）";
        return false;
    }

    private static bool IsWsl()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")))
            return true;
        if (Directory.Exists("/mnt/wslg") || Directory.Exists("/mnt/c/Windows"))
            return true;
        try
        {
            var ver = File.ReadAllText("/proc/version");
            if (ver.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                || ver.Contains("WSL", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    private static bool TryOpenViaWslExplorer(string directory, out string? error)
    {
        error = null;
        var winPath = TryMapToWindowsPath(directory);
        if (string.IsNullOrWhiteSpace(winPath))
        {
            error = "wslpath 无法转换路径";
            return false;
        }

        foreach (var explorer in ExplorerCandidates())
        {
            try
            {
                using var exp = Process.Start(new ProcessStartInfo
                {
                    FileName = explorer,
                    ArgumentList = { winPath },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                if (exp == null) continue;
                // explorer.exe 成功时也常返回 1
                _ = exp.WaitForExit(1500);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        error ??= "无法启动 explorer.exe";
        return false;
    }

    private static IEnumerable<string> ExplorerCandidates()
    {
        yield return "explorer.exe";
        yield return "/mnt/c/Windows/explorer.exe";
        yield return "/mnt/c/WINDOWS/explorer.exe";
    }

    private static string? TryMapToWindowsPath(string directory)
    {
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
            if (map != null)
            {
                var winPath = map.StandardOutput.ReadToEnd().Trim();
                map.WaitForExit(3000);
                if (map.ExitCode == 0 && !string.IsNullOrWhiteSpace(winPath))
                    return winPath;
            }
        }
        catch
        {
            /* fall through */
        }

        // 手工拼 \\wsl$\Distro\...
        var distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        if (string.IsNullOrWhiteSpace(distro))
            distro = TryReadWslDistroName();
        if (string.IsNullOrWhiteSpace(distro))
            return null;

        var unix = Path.GetFullPath(directory).Replace('\\', '/');
        if (!unix.StartsWith('/'))
            return null;
        var unc = $@"\\wsl$\{distro}{unix.Replace('/', '\\')}";
        return unc;
    }

    private static string? TryReadWslDistroName()
    {
        try
        {
            // /etc/hostname 有时是 DESKTOP-xxx-WSL，不是发行版名；优先 interop 环境
            var envFile = "/proc/sys/kernel/osrelease";
            _ = envFile;
        }
        catch
        {
            /* ignore */
        }

        // 常见：从 /mnt/c/ 存在时仍可用 wsl.exe -l
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl.exe",
                ArgumentList = { "-l", "-q", "--running" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            });
            if (p == null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim().TrimStart('\uFEFF');
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    private static bool TryOpenAsUser(string user, string directory, out string? error)
    {
        error = null;
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var xauth = Environment.GetEnvironmentVariable("XAUTHORITY");
        if (string.IsNullOrWhiteSpace(xauth))
        {
            var candidate = Path.Combine("/home", user, ".Xauthority");
            if (File.Exists(candidate))
                xauth = candidate;
        }

        var uid = TryResolveUid(user);
        var dbus = uid >= 0 ? $"unix:path=/run/user/{uid}/bus" : null;

        var envArgs = new List<string> { $"DISPLAY={display}" };
        if (!string.IsNullOrWhiteSpace(xauth))
            envArgs.Add($"XAUTHORITY={xauth}");
        if (!string.IsNullOrWhiteSpace(dbus))
            envArgs.Add($"DBUS_SESSION_BUS_ADDRESS={dbus}");

        // 先试资源管理器（用户会话），再 xdg-open
        foreach (var opener in new[] { "xdg-open", "nautilus", "dolphin", "thunar" })
        {
            var args = new List<string>(envArgs) { opener, directory };
            if (TryStartProcess("runuser", Prepend(["-u", user, "--", "env"], args), out error))
                return true;
            if (TryStartProcess("sudo", Prepend(["-n", "-u", user, "env"], args), out error))
                return true;
        }

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
                return true;

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
