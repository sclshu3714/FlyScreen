#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;

namespace FlyScreen.Tool.DeveloperTools.Ui.Maui;

/// <summary>Win32 选文件夹（MAUI FolderPicker 在 unpackaged/管理员下常失败）。</summary>
internal static class DevToolsWin32FolderPicker
{
    public static string? PickFolder(string? title = null)
    {
        var bi = new BrowseInfo
        {
            hwndOwner = GetActiveWindow(),
            lpszTitle = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title,
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

    private static class Bif
    {
        public const uint ReturnOnlyFsDirs = 0x00000001;
        public const uint NewDialogStyle = 0x00000040;
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
#endif
