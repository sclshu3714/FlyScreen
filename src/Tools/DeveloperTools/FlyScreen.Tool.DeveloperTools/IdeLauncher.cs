using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlyScreen.Tool.DeveloperTools;

/// <summary>用 VS Code / Visual Studio 打开生成的工程目录。</summary>
public static class IdeLauncher
{
    public static bool TryOpenVsCode(string folder, out string error)
    {
        error = "";
        if (!Directory.Exists(folder))
        {
            error = "目录不存在";
            return false;
        }

        // 打开文件夹（含 .sln 与全部子工程）；VS Code 用文件夹工作区即可看到全部项目
        foreach (var exe in new[] { "code", "code.cmd" })
        {
            if (TryStart(exe, Quote(folder), out error))
                return true;
        }

        error = string.IsNullOrEmpty(error) ? "未找到 VS Code（code）" : error;
        return false;
    }

    public static bool TryOpenVisualStudio(string folder, out string error)
    {
        error = "";
        if (!Directory.Exists(folder))
        {
            error = "目录不存在";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            error = "Visual Studio 仅支持 Windows";
            return false;
        }

        var devenv = TryFindDevenv();
        if (devenv == null)
        {
            error = "未找到 Visual Studio（devenv）";
            return false;
        }

        // 必须打开聚合 .sln；勿打开单个 .csproj（VS 会生成只含 1 个项目的临时解决方案）
        var sln = Directory.EnumerateFiles(folder, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sln == null)
        {
            error = "未找到 .sln，请重新用开发者工具生成脚手架";
            return false;
        }
        return TryStart(devenv, Quote(sln), out error);
    }

    public static bool TryOpenFolder(string folder, out string error)
    {
        error = "";
        if (!Directory.Exists(folder))
        {
            error = "目录不存在";
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = Quote(folder),
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", Quote(folder));
            }
            else
            {
                Process.Start("xdg-open", Quote(folder));
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? TryFindDevenv()
    {
        try
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return null;
            var psi = new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-latest -products * -requires Microsoft.Component.MSBuild -property productPath",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            var path = p?.StandardOutput.ReadToEnd()?.Trim();
            p?.WaitForExit(15_000);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool TryStart(string fileName, string args, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Quote(string path)
        => "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
