using System.IO;
using System.Runtime.InteropServices;

namespace FlyScreen.Installer.Services;

public static class DiskSpace
{
    public static bool TryGetFreeBytes(string path, out long freeBytes)
    {
        freeBytes = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            return GetDiskFreeSpaceEx(root, out freeBytes, out _, out _);
        }
        catch
        {
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0.##} MB";
        if (bytes >= kb) return $"{bytes / kb:0.##} KB";
        return $"{bytes} B";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out long lpFreeBytesAvailable,
        out long lpTotalNumberOfBytes,
        out long lpTotalNumberOfFreeBytes);
}
