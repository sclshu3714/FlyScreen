using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.ToolSdk;

namespace FlyScreen.SharedDevices;

/// <summary>Windows：WASAPI 枚举扬声器/麦克风；SetupAPI（照相机类）枚举摄像头。</summary>
public sealed class WindowsSharedDevicesHardware : ISharedDevicesHardware
{
    /// <summary>GUID_DEVCLASS_CAMERA — 设备管理器「照相机」。</summary>
    private static readonly Guid GuidDevClassCamera = new("ca3e32d7-a3b0-4f7b-8e9d-2e8c7c8e0c5e");

    /// <summary>KSCATEGORY_VIDEO_CAMERA — 视频采集设备接口。</summary>
    private static readonly Guid KsCategoryVideoCamera = new("e5323777-f976-4f5b-9b55-b94699c46e44");

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
        // 现代 UVC 在设备管理器「照相机」类；合并设备接口枚举，避免只命中一类
        var list = EnumCamerasBySetupClass();
        foreach (var d in EnumCamerasByVideoInterface())
        {
            if (list.Any(x => x.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase)
                              || x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(new SharedLocalDevice
            {
                Kind = d.Kind,
                Id = d.Id,
                Name = d.Name,
                IsDefault = list.Count == 0
            });
        }

        return list;
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

    private static List<SharedLocalDevice> EnumCamerasByVideoInterface()
    {
        var list = new List<SharedLocalDevice>();
        var guid = KsCategoryVideoCamera;
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

                var id = GetDeviceInstanceId(info, ref devData) ?? path;
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
}

