using System.Runtime.InteropServices;

namespace FlyScreen.WinApi;

/// <summary>
/// 仅改任务栏/Shell 显示名，不改 exe / AssemblyName。
/// Win / Maui / TopologyGuard 共用。
/// </summary>
public static class WindowsAppUserModel
{
    public const string MainAppId = "XuanNiao.FlyScreen";
    public const string MainDisplayName = "玄鸟";
    public const string GuardAppId = "XuanNiao.FlyScreen.TopologyGuard";
    public const string GuardDisplayName = "玄鸟:守护";

    private static readonly HashSet<nint> AppliedHwnds = [];
    private static string? _processId;
    private static string? _appliedRelaunch;

    /// <summary>须在任何窗口创建之前调用。</summary>
    public static void EnsureProcessId(string appId)
    {
        if (string.Equals(_processId, appId, StringComparison.Ordinal))
            return;
        try
        {
            if (SetCurrentProcessExplicitAppUserModelID(appId) == 0)
                _processId = appId;
        }
        catch
        {
            /* ignore */
        }
    }

    public static void ApplyToWindow(IntPtr hwnd, string appId, string displayName, params string[] iconCandidates)
    {
        if (hwnd == IntPtr.Zero)
            return;

        EnsureProcessId(appId);

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return;

            // 同一窗口若启动路径已写入过则跳过；路径变化（如从 Debug 换到安装目录）须重写
            var relaunch = exe.Contains(' ') ? $"\"{exe}\"" : exe;
            if (AppliedHwnds.Contains(hwnd)
                && string.Equals(_appliedRelaunch, relaunch, StringComparison.OrdinalIgnoreCase))
                return;

            var iid = typeof(IPropertyStore).GUID;
            var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var punk);
            if (hr != 0 || punk == IntPtr.Zero)
                return;

            IPropertyStore? store = null;
            try
            {
                store = (IPropertyStore)Marshal.GetUniqueObjectForIUnknown(punk);
                var icon = ResolveIconResource(exe, iconCandidates);

                SetString(store, PkeyId, appId);
                SetString(store, PkeyRelaunchCommand, relaunch);
                SetString(store, PkeyRelaunchDisplayName, displayName);
                if (icon != null)
                    SetString(store, PkeyRelaunchIcon, icon);
                store.Commit();
                AppliedHwnds.Add(hwnd);
                _appliedRelaunch = relaunch;
            }
            finally
            {
                if (store != null)
                {
                    try { Marshal.FinalReleaseComObject(store); } catch { /* ignore */ }
                }

                Marshal.Release(punk);
            }
        }
        catch
        {
            AppliedHwnds.Remove(hwnd);
        }
    }

    private static string? ResolveIconResource(string exe, string[] iconCandidates)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in iconCandidates)
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var full = Path.IsPathRooted(rel)
                ? rel
                : Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(full))
                return full;
        }

        return exe + ",0";
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

    private static readonly PropertyKey PkeyRelaunchCommand = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 2);
    private static readonly PropertyKey PkeyRelaunchIcon = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 3);
    private static readonly PropertyKey PkeyRelaunchDisplayName = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 4);
    private static readonly PropertyKey PkeyId = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 5);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IntPtr ppv);

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

    /// <summary>
    /// x86=16 字节、x64=24 字节（union 对齐）。旧布局只有 16 字节会在 SetValue 时 AV。
    /// </summary>
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
}
