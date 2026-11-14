using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.ToolSdk;

namespace FlyScreen.SharedDevices;

/// <summary>Windows：WASAPI 枚举扬声器/麦克风；SetupAPI（照相机类）枚举摄像头。</summary>
public sealed class WindowsSharedDevicesHardware : ISharedDevicesHardware
{
    /// <summary>GUID_DEVCLASS_CAMERA — 设备管理器「照相机」。</summary>
    private static readonly Guid GuidDevClassCamera = new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");

    /// <summary>KSCATEGORY_VIDEO_CAMERA — 视频采集设备接口。</summary>
    private static readonly Guid KsCategoryVideoCamera = new("e5323777-f976-4f5b-9b55-b94699c46e44");

    /// <summary>KSCATEGORY_CAPTURE — 部分 UVC 只挂在此类。</summary>
    private static readonly Guid KsCategoryCapture = new("65e8773d-8f56-11d0-a3b9-00a0c9223196");

    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind)
    {
        if (kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase))
            return EnumCameras();
        if (kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase))
            return EnumAudio(capture: true);
        if (kind.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase))
            return EnumAudio(capture: false);
        return [];
    }

    private static IReadOnlyList<SharedLocalDevice> EnumAudio(bool capture)
    {
        var list = new List<SharedLocalDevice>();
        try
        {
            // UI 线程常为 STA；忽略 RPC_E_CHANGED_MODE，继续用已有套间
            _ = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
            IMMDeviceEnumerator en;
            try
            {
                // Activator/CLSID 比 CoCreateInstance+is 在 MAUI 下更稳
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            }
            catch
            {
                return [];
            }

            var dataFlow = capture ? 1 : 0;
            const uint deviceStateActive = 0x1;
            if (en.EnumAudioEndpoints(dataFlow, deviceStateActive, out var collPtr) != 0 || collPtr == IntPtr.Zero)
                return [];

            var coll = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collPtr);
            Marshal.Release(collPtr);
            coll.GetCount(out var count);

            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(dataFlow, 0, out var defDev) == 0 && defDev != null
                && defDev.GetId(out var defIdPtr) == 0 && defIdPtr != IntPtr.Zero)
            {
                defaultId = Marshal.PtrToStringUni(defIdPtr);
                Marshal.FreeCoTaskMem(defIdPtr);
            }

            for (uint i = 0; i < count; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev == null) continue;
                if (dev.GetId(out var idPtr) != 0 || idPtr == IntPtr.Zero) continue;
                var id = Marshal.PtrToStringUni(idPtr) ?? "";
                Marshal.FreeCoTaskMem(idPtr);
                var name = ReadFriendlyName(dev) ?? $"设备 {i + 1}";
                list.Add(new SharedLocalDevice
                {
                    Kind = capture ? SharedDeviceKinds.Microphone : SharedDeviceKinds.Speaker,
                    Id = id,
                    Name = name,
                    IsDefault = !string.IsNullOrEmpty(defaultId)
                        && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch
        {
            return [];
        }

        // 无活动端点则如实返回空，勿伪造「系统默认」（否则无硬件机仍可被申请）
        if (list.Count == 0)
            return list;
        if (!list.Any(d => d.IsDefault))
        {
            var first = list[0];
            list[0] = new SharedLocalDevice
            {
                Kind = first.Kind,
                Id = first.Id,
                Name = first.Name,
                IsDefault = true
            };
        }

        return list;
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0, out var storePtr) != 0 || storePtr == IntPtr.Zero)
                return null;
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
            Marshal.Release(storePtr);
            var key = PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out var pv) != 0)
                return null;
            try
            {
                if (pv.vt == 31 && pv.p != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pv.p);
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static IReadOnlyList<SharedLocalDevice> EnumCameras()
    {
        // 优先符号链接（MF/MediaCapture 可打开）；再补照相机类实例 Id
        var list = EnumCamerasByDeviceInterface(KsCategoryVideoCamera);
        foreach (var d in EnumCamerasByDeviceInterface(KsCategoryCapture))
        {
            if (list.Any(x => x.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase)
                              || x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)
                              || IdsLikelySameCamera(x.Id, d.Id)))
                continue;
            list.Add(d);
        }

        foreach (var d in EnumCamerasBySetupClass())
        {
            if (list.Any(x => x.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase)
                              || x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)
                              || IdsLikelySameCamera(x.Id, d.Id)))
                continue;
            // 尝试把实例 Id 换成已有符号链接，避免 UI 选中后无法打开
            var link = ListCameraSymbolicLinks(d.Id).FirstOrDefault(IsSymbolicLinkPath);
            list.Add(new SharedLocalDevice
            {
                Kind = d.Kind,
                Id = link ?? d.Id,
                Name = d.Name,
                IsDefault = false
            });
        }

        if (list.Count > 0 && !list.Any(d => d.IsDefault))
        {
            var first = list[0];
            list[0] = new SharedLocalDevice
            {
                Kind = first.Kind,
                Id = first.Id,
                Name = first.Name,
                IsDefault = true
            };
        }

        return list;
    }

    /// <summary>
    /// 供采集端打开：返回视频捕获符号链接（\\?\...），按 prefer（符号链接或实例 Id）排序。
    /// </summary>
    public static IReadOnlyList<string> ListCameraSymbolicLinks(string? prefer = null)
    {
        var links = new List<string>();
        void AddLink(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!IsSymbolicLinkPath(id)) return;
            if (links.Any(x => x.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
            links.Add(id);
        }

        foreach (var d in EnumCamerasByDeviceInterface(KsCategoryVideoCamera))
            AddLink(d.Id);
        foreach (var d in EnumCamerasByDeviceInterface(KsCategoryCapture))
            AddLink(d.Id);
        foreach (var path in EnumDirectShowCameraDevicePaths())
            AddLink(path);

        // 选用的若已是符号链接也纳入
        AddLink(prefer);

        if (string.IsNullOrEmpty(prefer) || links.Count <= 1)
            return links;

        return links
            .OrderByDescending(l => IdsLikelySameCamera(l, prefer!) ||
                                    l.Equals(prefer, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsSymbolicLinkPath(string id)
        => id.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
           || id.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
           || id.StartsWith(@"\??\", StringComparison.Ordinal);

    private static bool IdsLikelySameCamera(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        // 符号链接里常含规范化后的实例片段；实例 Id 形如 USB\VID_xxxx&PID_xxxx\...
        var na = NormalizeDeviceKey(a);
        var nb = NormalizeDeviceKey(b);
        return na.Length >= 12 && nb.Length >= 12
               && (na.Contains(nb, StringComparison.OrdinalIgnoreCase)
                   || nb.Contains(na, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDeviceKey(string id)
    {
        var s = id.Replace('\\', '#').Replace('/', '#').ToUpperInvariant();
        // 去掉 \\?\ / \\.\ 前缀噪音，便于实例 Id 与符号链接互认
        s = s.Replace(@"#?#USB#", "#USB#", StringComparison.Ordinal)
             .Replace(@"#?#", "#", StringComparison.Ordinal);
        return s;
    }

    private static List<SharedLocalDevice> EnumCamerasBySetupClass()
    {
        var list = new List<SharedLocalDevice>();
        var guid = GuidDevClassCamera;
        var info = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (info == IntPtr.Zero || info == new IntPtr(-1))
            return list;

        try
        {
            var data = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(info, i, ref data); i++)
            {
                var name = GetDeviceRegistryString(info, ref data, SpdrpFriendlyName)
                           ?? GetDeviceRegistryString(info, ref data, SpdrpDevdesc);
                if (string.IsNullOrWhiteSpace(name) || IsPhantomCameraDriver(name))
                    continue;

                var id = GetDeviceInstanceId(info, ref data) ?? $"camera-class-{i}";
                if (list.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                list.Add(new SharedLocalDevice
                {
                    Kind = SharedDeviceKinds.Camera,
                    Id = id,
                    Name = name.Trim(),
                    IsDefault = list.Count == 0
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }

        return list;
    }

    private static List<SharedLocalDevice> EnumCamerasByDeviceInterface(Guid category)
    {
        var list = new List<SharedLocalDevice>();
        var guid = category;
        var info = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (info == IntPtr.Zero || info == new IntPtr(-1))
            return list;

        try
        {
            var ifData = new SpDeviceInterfaceData
            {
                cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
            };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                if (!TryGetInterfaceDetail(info, ref ifData, out var path, out var devData))
                    continue;

                var name = GetDeviceRegistryString(info, ref devData, SpdrpFriendlyName)
                           ?? GetDeviceRegistryString(info, ref devData, SpdrpDevdesc)
                           ?? path;
                if (string.IsNullOrWhiteSpace(name) || IsPhantomCameraDriver(name))
                    continue;

                var instanceId = GetDeviceInstanceId(info, ref devData);
                var id = !string.IsNullOrEmpty(path) ? path : (instanceId ?? $"camera-if-{i}");
                if (list.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                                  || (!string.IsNullOrEmpty(instanceId)
                                      && d.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))))
                    continue;

                list.Add(new SharedLocalDevice
                {
                    Kind = SharedDeviceKinds.Camera,
                    Id = id,
                    Name = name.Trim(),
                    IsDefault = list.Count == 0
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }

        return list;
    }

    /// <summary>DirectShow 视频输入设备的 DevicePath（常比 SetupAPI 更全）。</summary>
    private static List<string> EnumDirectShowCameraDevicePaths()
    {
        var list = new List<string>();
        try
        {
            _ = CoInitializeEx(IntPtr.Zero, 0x2);
            var clsid = ClsidSystemDeviceEnum;
            var iid = typeof(ICreateDevEnum).GUID;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var obj) != 0 || obj == null)
                return list;
            var create = (ICreateDevEnum)obj;
            var cat = ClsidVideoInputDeviceCategory;
            if (create.CreateClassEnumerator(ref cat, out var enumMon, 0) != 0 || enumMon == null)
                return list;

            var monikers = new IMoniker[1];
            while (enumMon.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var mon = monikers[0];
                if (mon == null) continue;
                try
                {
                    var iidBag = IidPropertyBag;
                    if (mon.BindToStorage(IntPtr.Zero, IntPtr.Zero, ref iidBag, out var bagObj) != 0
                        || bagObj == null)
                        continue;
                    var bag = (IPropertyBag)bagObj;
                    try
                    {
                        object pathObj = "";
                        if (bag.Read("DevicePath", ref pathObj, IntPtr.Zero) != 0)
                            continue;
                        if (pathObj is string path && !string.IsNullOrWhiteSpace(path))
                            list.Add(path.Trim());
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(bag);
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    Marshal.ReleaseComObject(mon);
                }
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static bool TryGetInterfaceDetail(
        IntPtr info, ref SpDeviceInterfaceData ifData, out string path, out SpDevinfoData devData)
    {
        path = "";
        devData = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
        uint needed = 0;
        SetupDiGetDeviceInterfaceDetail(info, ref ifData, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
        if (needed == 0)
            return false;

        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize：x64=8，x86 Unicode=6
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
            if (!SetupDiGetDeviceInterfaceDetail(info, ref ifData, buf, needed, ref needed, ref devData))
                return false;

            var pathOffset = IntPtr.Size == 8 ? 8 : 4;
            path = Marshal.PtrToStringUni(buf + pathOffset) ?? "";
            return !string.IsNullOrEmpty(path);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? GetDeviceRegistryString(IntPtr info, ref SpDevinfoData data, uint prop)
    {
        uint regType = 0;
        uint needed = 0;
        SetupDiGetDeviceRegistryProperty(info, ref data, prop, ref regType, IntPtr.Zero, 0, ref needed);
        if (needed == 0)
            return null;

        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!SetupDiGetDeviceRegistryProperty(info, ref data, prop, ref regType, buf, needed, ref needed))
                return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? GetDeviceInstanceId(IntPtr info, ref SpDevinfoData data)
    {
        var sb = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceId(info, ref data, sb, sb.Capacity, out _))
            return null;
        return sb.ToString();
    }

    private static bool IsPhantomCameraDriver(string name)
        => name.Contains("WDM Image Capture", StringComparison.OrdinalIgnoreCase);

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint SpdrpDevdesc = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        ref uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        ref uint requiredSize, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property,
        ref uint propertyRegDataType, IntPtr propertyBuffer, uint propertyBufferSize,
        ref uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, out uint requiredSize);

    private static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, int ctx, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid type, out IEnumMoniker? enumMoniker, int flags);
    }

    [ComImport, Guid("00000102-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumMoniker
    {
        [PreserveSig] int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IMoniker[] rgelt, IntPtr pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumMoniker enumMoniker);
    }

    // IMoniker : IPersistStream : IPersist — 虚表按继承顺序
    [ComImport, Guid("0000000F-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMoniker
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(IntPtr pStm);
        [PreserveSig] int Save(IntPtr pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty);
        [PreserveSig] int GetSizeMax(out long pcbSize);
        [PreserveSig] int BindToObject(IntPtr pbc, IntPtr pmkToLeft, ref Guid riidResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppvResult);
        [PreserveSig] int BindToStorage(IntPtr pbc, IntPtr pmkToLeft, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObj);
    }

    private static readonly Guid IidPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string propName,
            [In, Out, MarshalAs(UnmanagedType.Struct)] ref object var, IntPtr errorLog);
        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string propName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object var);
    }
}

