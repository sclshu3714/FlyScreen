using System.Runtime.InteropServices;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Win32 文件选择（各 TFM 均可编译；仅在 Windows 运行时调用）。
/// MAUI <see cref="FilePicker"/> 在 unpackaged / 管理员下常 COMException；
/// 打进 flytool 的 net10.0 程序集无 <c>WINDOWS</c> 宏，不能依赖 <c>#if WINDOWS</c>。
/// </summary>
internal static class VideoWallWin32PathPicker
{
    public static string? PickSingleFile(string? title = null, string? filter = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32 文件选择仅支持 Windows");

        const int nMax = 260;
        var buffer = Marshal.AllocHGlobal(nMax * sizeof(char));
        try
        {
            var zero = new byte[nMax * sizeof(char)];
            Marshal.Copy(zero, 0, buffer, zero.Length);

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = GetActiveWindow(),
                lpstrFilter = string.IsNullOrWhiteSpace(filter)
                    ? "视频 (*.mp4;*.mkv;*.avi;*.mov;*.ts;*.m4v;*.wmv;*.flv;*.webm)\0*.mp4;*.mkv;*.avi;*.mov;*.ts;*.m4v;*.wmv;*.flv;*.webm\0所有文件 (*.*)\0*.*\0\0"
                    : filter,
                lpstrFile = buffer,
                nMaxFile = nMax,
                lpstrTitle = string.IsNullOrWhiteSpace(title) ? "选择文件" : title,
                Flags = Of.Explorer | Of.FileMustExist | Of.PathMustExist | Of.NoChangeDir
            };

            if (!GetOpenFileNameW(ref ofn))
                return null;

            var managed = new char[nMax];
            Marshal.Copy(buffer, managed, 0, nMax);
            var end = Array.IndexOf(managed, '\0');
            if (end < 0) end = managed.Length;
            var path = new string(managed, 0, end).Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class Of
    {
        public const int Explorer = 0x00080000;
        public const int FileMustExist = 0x00001000;
        public const int PathMustExist = 0x00000800;
        public const int NoChangeDir = 0x00000008;
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

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
