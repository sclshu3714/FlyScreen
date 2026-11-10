using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.ToolSdk;

namespace FlyScreen.SharedDevices;

/// <summary>Windows：WASAPI 枚举扬声器/麦克风；VFW 枚举摄像头。</summary>
public sealed class WindowsSharedDevicesHardware : ISharedDevicesHardware
{
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
            CoInitializeEx(IntPtr.Zero, 0);
            var clsid = typeof(MMDeviceEnumeratorComObject).GUID;
            var iid = typeof(IMMDeviceEnumerator).GUID;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var obj) != 0
                || obj is not IMMDeviceEnumerator en)
                return [];

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
        var list = new List<SharedLocalDevice>();
        var name = new StringBuilder(256);
        var ver = new StringBuilder(256);
        for (short i = 0; i < 10; i++)
        {
            name.Clear();
            ver.Clear();
            if (!capGetDriverDescriptionA(i, name, name.Capacity, ver, ver.Capacity))
                break;
            var n = name.ToString().Trim();
            if (string.IsNullOrEmpty(n))
                n = $"摄像头 {i + 1}";
            // 无物理摄像头时 VFW 常仍返回此驱动名，不算可用设备
            if (IsPhantomCameraDriver(n))
                continue;
            list.Add(new SharedLocalDevice
            {
                Kind = SharedDeviceKinds.Camera,
                Id = i.ToString(),
                Name = n,
                IsDefault = list.Count == 0
            });
        }

        return list;
    }

    private static bool IsPhantomCameraDriver(string name)
        => name.Contains("WDM Image Capture", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Microsoft WDM Image Capture (Win32)", StringComparison.OrdinalIgnoreCase);

    private static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
    private static extern bool capGetDriverDescriptionA(
        short index, StringBuilder name, int nameLen, StringBuilder ver, int verLen);

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
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}

