using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlyScreen.Helpers;

/// <summary>在文件管理器中打开路径所在文件夹（尽量选中文件）。</summary>
public static class PathReveal
{
    public static bool TryReveal(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var isDir = Directory.Exists(path);
        var isFile = File.Exists(path);
        if (!isDir && !isFile)
        {
            error = "文件或文件夹不存在";
            return false;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (isFile)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + path + "\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }

                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return isFile
                    ? Start("open", ["-R", path], out error)
                    : Start("open", [path], out error);
            }

            var dir = isDir ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
            {
                error = "无法解析目录";
                return false;
            }

            return Start("xdg-open", [dir], out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool Start(string fileName, IReadOnlyList<string> args, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
