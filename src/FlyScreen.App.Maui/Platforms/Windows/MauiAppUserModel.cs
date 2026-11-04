#if WINDOWS
using System.Runtime.InteropServices;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 仅改「显示名称」，不改 exe / AssemblyName。
/// 任务栏右键顶栏、固定磁贴文案来自 AppUserModel.RelaunchDisplayNameResource；
/// 进程名列仍为 FlyScreen.App.Maui.exe。
/// </summary>
internal static class MauiAppUserModel
{
    public const string AppId = "XuanNiao.FlyScreen";
    public const string DisplayName = "玄鸟";

    private static readonly HashSet<nint> AppliedHwnds = [];
    private static bool _processIdSet;

    /// <summary>须在任何窗口创建之前调用。</summary>
    public static void EnsureProcessId()
    {
        if (_processIdSet) return;
        try
        {
            if (SetCurrentProcessExplicitAppUserModelID(AppId) == 0)
                _processIdSet = true;
        }
        catch
        {
            /* ignore */
        }
    }

    public static void ApplyToWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !AppliedHwnds.Add(hwnd))
            return;

        EnsureProcessId();

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return;

            var hr = SHGetPropertyStoreForWindow(hwnd, typeof(IPropertyStore).GUID, out var store);
            if (hr != 0 || store is null) return;

            try
            {
                var relaunch = exe.Contains(' ') ? $"\"{exe}\"" : exe;
                var icon = ResolveIconResource(exe);

                SetString(store, PkeyId, AppId);
                SetString(store, PkeyRelaunchCommand, relaunch);
                SetString(store, PkeyRelaunchDisplayName, DisplayName);
                if (icon != null)
                    SetString(store, PkeyRelaunchIcon, icon);
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            AppliedHwnds.Remove(hwnd);
        }
    }

    private static string? ResolveIconResource(string exe)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[]
                 {
                     Path.Combine("Assets", "appicon.ico"),
                     Path.Combine("Resources", "AppIcon", "appicon.ico"),
                     "appicon.ico"
                 })
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(full))
                return full;
        }

        // 回退：exe 内嵌图标
        return exe + ",0";
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant();
        try
        {
            pv.vt = (ushort)VarEnum.VT_LPWSTR;
            pv.union.pszVal = Marshal.StringToCoTaskMemUni(value);
            store.SetValue(ref key, ref pv);
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    // PKEY_AppUserModel_* — formatID 9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3
    private static readonly PropertyKey PkeyRelaunchCommand = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 2);
    private static readonly PropertyKey PkeyRelaunchIcon = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 3);
    private static readonly PropertyKey PkeyRelaunchDisplayName = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 4);
    private static readonly PropertyKey PkeyId = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 5);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IPropertyStore ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

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

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariantUnion
    {
        [FieldOffset(0)] public IntPtr pszVal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public PropVariantUnion union;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PropertyKey pkey);
        uint GetValue(ref PropertyKey key, out PropVariant pv);
        uint SetValue(ref PropertyKey key, ref PropVariant pv);
        uint Commit();
    }
}
#endif
