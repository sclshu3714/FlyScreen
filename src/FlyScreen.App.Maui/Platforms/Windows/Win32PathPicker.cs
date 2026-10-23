using System.Runtime.InteropServices;
using System.Text;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 经典 Win32 文件/文件夹选择。
/// MAUI <see cref="FilePicker"/> 在 unpackaged + 管理员权限下会 COMException。
/// </summary>
internal static class Win32PathPicker
{
    public static IReadOnlyList<string> PickFiles(bool multiSelect = true)
    {
        const int chars = 64 * 1024;
        var buffer = Marshal.AllocHGlobal((multiSelect ? chars : 260) * sizeof(char));
        var nMax = multiSelect ? chars : 260;
        try
        {
            // 清零缓冲区（多选路径用双 null 结尾）
            var zero = new byte[nMax * sizeof(char)];
            Marshal.Copy(zero, 0, buffer, zero.Length);

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = GetActiveWindow(),
                lpstrFilter = "所有文件 (*.*)\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = nMax,
                lpstrTitle = "选择要传输的文件",
                Flags = Of.Explorer | Of.FileMustExist | Of.PathMustExist | Of.NoChangeDir
                        | (multiSelect ? Of.AllowMultiSelect : 0)
            };

            if (!GetOpenFileNameW(ref ofn))
                return [];

            var managed = new char[nMax];
            Marshal.Copy(buffer, managed, 0, nMax);
            return ParseMultiSelect(managed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? PickFolder()
    {
        var bi = new BrowseInfo
        {
            hwndOwner = GetActiveWindow(),
            lpszTitle = "选择要传输的文件夹",
            ulFlags = Bif.NewDialogStyle | Bif.ReturnOnlyFsDirs
        };

        var pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(260);
            return SHGetPathFromIDListW(pidl, sb) ? sb.ToString() : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static List<string> ParseMultiSelect(char[] buf)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] != '\0') continue;
            if (i == start) break;
            parts.Add(new string(buf, start, i - start));
            start = i + 1;
        }

        if (parts.Count <= 1) return parts;
        var dir = parts[0];
        var files = new List<string>(parts.Count - 1);
        for (var i = 1; i < parts.Count; i++)
            files.Add(Path.Combine(dir, parts[i]));
        return files;
    }

    private static class Of
    {
        public const int AllowMultiSelect = 0x00000200;
        public const int Explorer = 0x00080000;
        public const int FileMustExist = 0x00001000;
        public const int PathMustExist = 0x00000800;
        public const int NoChangeDir = 0x00000008;
    }

    private static class Bif
    {
        public const uint NewDialogStyle = 0x00000040;
        public const uint ReturnOnlyFsDirs = 0x00000001;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
