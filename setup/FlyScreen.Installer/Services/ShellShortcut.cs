using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FlyScreen.Installer.Services;

/// <summary>
/// 创建带 AppUserModelID 的 .lnk，确保任务栏「属性」关联到安装目录，
/// 而不是同 AUMID 下历史 Debug 运行留下的目标。
/// </summary>
internal static class ShellShortcut
{
    /// <summary>与 FlyScreen.WinApi.WindowsAppUserModel.MainAppId 保持一致。</summary>
    public const string MainAppUserModelId = "XuanNiao.FlyScreen";

    public static void Create(
        string linkPath,
        string targetPath,
        string workingDir,
        string description,
        string appUserModelId = MainAppUserModelId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        if (!TryCreateWithAumid(linkPath, targetPath, workingDir, description, appUserModelId))
            CreateFallback(linkPath, targetPath, workingDir, description);
    }

    private static bool TryCreateWithAumid(
        string linkPath,
        string targetPath,
        string workingDir,
        string description,
        string appUserModelId)
    {
        IShellLinkW? link = null;
        IPersistFile? persist = null;
        IPropertyStore? store = null;
        try
        {
            link = (IShellLinkW)new CShellLink();
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description ?? "");
            link.SetIconLocation(targetPath, 0);

            store = (IPropertyStore)link;
            SetString(store, PkeyAppUserModelId, appUserModelId);
            store.Commit();

            persist = (IPersistFile)link;
            persist.Save(linkPath, true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (store != null) try { Marshal.FinalReleaseComObject(store); } catch { /* ignore */ }
            if (persist != null) try { Marshal.FinalReleaseComObject(persist); } catch { /* ignore */ }
            if (link != null) try { Marshal.FinalReleaseComObject(link); } catch { /* ignore */ }
        }
    }

    private static void CreateFallback(string linkPath, string targetPath, string workingDir, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            var bat = Path.ChangeExtension(linkPath, ".bat");
            File.WriteAllText(bat, $"@start \"\" /D \"{workingDir}\" \"{targetPath}\"", Encoding.Default);
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDir;
        shortcut.Description = description ?? "";
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant
        {
            vt = (ushort)VarEnum.VT_LPWSTR,
            pszVal = Marshal.StringToCoTaskMemUni(value)
        };
        try
        {
            store.SetValue(ref key, ref pv);
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    // PKEY_AppUserModel_ID
    private static readonly PropertyKey PkeyAppUserModelId =
        new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 5);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public PropertyKey(uint a, ushort b, ushort c, byte d0, byte d1, byte d2, byte d3, byte d4, byte d5, byte d6, byte d7, uint propId)
        {
            fmtid = new Guid(a, b, c, d0, d1, d2, d3, d4, d5, d6, d7);
            pid = propId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pszVal;
        public IntPtr reserved;
    }
}
