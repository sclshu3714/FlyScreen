using System.Runtime.InteropServices;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

internal static class FileVaultMauiDialogs
{
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosAllowMultiSelect = 0x200;
    private const uint FosPathMustExist = 0x800;
    private const uint FosFileMustExist = 0x1000;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int HrCancelled = unchecked((int)0x800704C7);

    public static Task<string?> PickFolderAsync(string title, Window? owner)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<string?>(null);
        var path = Pick(OwnerHwnd(owner), title, folders: true, filter: null).FirstOrDefault();
        return Task.FromResult(path);
    }

    public static Task<IReadOnlyList<string>> PickFilesAsync(Window? owner, string title, string? filterName = null, string? filterSpec = null)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<string>>([]);
        (string Name, string Spec)? filter = string.IsNullOrWhiteSpace(filterSpec)
            ? null
            : (filterName ?? filterSpec, filterSpec);
        return Task.FromResult(Pick(OwnerHwnd(owner), title, folders: false, filter));
    }

    private static IntPtr OwnerHwnd(Window? owner)
    {
        var native = owner?.Handler?.PlatformView;
        var prop = native?.GetType().GetProperty("WindowHandle");
        return prop?.GetValue(native) switch
        {
            IntPtr p => p,
            long l => new IntPtr(l),
            _ => IntPtr.Zero
        };
    }

    private static IReadOnlyList<string> Pick(IntPtr owner, string title, bool folders, (string Name, string Spec)? filter)
    {
        var dlg = (IFileOpenDialog)new FileOpenDialogRcw();
        dlg.GetOptions(out var options);
        options |= FosForceFileSystem | FosPathMustExist;
        options |= folders ? FosPickFolders : FosAllowMultiSelect | FosFileMustExist;
        dlg.SetOptions(options);
        if (!string.IsNullOrWhiteSpace(title))
            dlg.SetTitle(title);
        if (filter is { } spec)
        {
            var specs = new[]
            {
                new FilterSpec { Name = spec.Name, Spec = spec.Spec },
                new FilterSpec { Name = "所有文件", Spec = "*.*" }
            };
            dlg.SetFileTypes((uint)specs.Length, specs);
        }

        var hr = dlg.Show(owner);
        if (hr == HrCancelled)
            return [];
        Marshal.ThrowExceptionForHR(hr);

        if (folders)
        {
            dlg.GetResult(out var item);
            item.GetDisplayName(SigdnFileSysPath, out var path);
            return string.IsNullOrEmpty(path) ? [] : [path];
        }

        dlg.GetResults(out var items);
        items.GetCount(out var count);
        var paths = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            items.GetItemAt(i, out var item);
            item.GetDisplayName(SigdnFileSysPath, out var path);
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }
        return paths;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw
    {
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PropertyKey key, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int flags, uint mask, out uint attrs);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr enumerator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }
}
