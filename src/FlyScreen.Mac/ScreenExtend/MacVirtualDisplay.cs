using System.Runtime.InteropServices;
using FlyScreen.Mac.Permissions;

namespace FlyScreen.Mac.ScreenExtend;

/// <summary>
/// 用 CGVirtualDisplay 建一块虚拟副屏。没有第二块物理屏时，扩展投影靠它。
/// 对象必须一直留着，释放后显示器消失。
/// </summary>
internal sealed class MacVirtualDisplay : IDisposable
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string System = "/usr/lib/libSystem.dylib";

    private IntPtr _display;
    private uint _id;

    public uint DisplayId => _id;

    public static bool IsSupported
    {
        get
        {
            if (!AccessibilityPermission.IsMacOS) return false;
            try { return objc_getClass("CGVirtualDisplay") != IntPtr.Zero; }
            catch { return false; }
        }
    }

    public bool TryCreate(int pixelWidth, int pixelHeight, int refreshHz, out string? error)
    {
        error = null;
        if (!AccessibilityPermission.IsMacOS)
        {
            error = "非 macOS";
            return false;
        }

        pixelWidth = Math.Clamp(pixelWidth, 640, 3840);
        pixelHeight = Math.Clamp(pixelHeight, 480, 2160);
        if (refreshHz <= 0) refreshHz = 60;

        IntPtr pool = IntPtr.Zero;
        try
        {
            pool = objc_autoreleasePoolPush();
            var descCls = objc_getClass("CGVirtualDisplayDescriptor");
            var dispCls = objc_getClass("CGVirtualDisplay");
            var modeCls = objc_getClass("CGVirtualDisplayMode");
            var settingsCls = objc_getClass("CGVirtualDisplaySettings");
            if (descCls == IntPtr.Zero || dispCls == IntPtr.Zero || modeCls == IntPtr.Zero || settingsCls == IntPtr.Zero)
            {
                error = "当前 macOS 不能创建虚拟副屏，请连接第二块屏幕后再扩展。";
                return false;
            }

            var desc = Send(Send(descCls, Sel("alloc")), Sel("init"));
            if (desc == IntPtr.Zero)
            {
                error = "无法创建虚拟显示器描述。";
                return false;
            }

            var name = NsString("玄鸟");
            if (name != IntPtr.Zero)
                Send1(desc, Sel("setName:"), name);
            Send1L(desc, Sel("setMaxPixelsWide:"), pixelWidth);
            Send1L(desc, Sel("setMaxPixelsHigh:"), pixelHeight);
            Send1L(desc, Sel("setProductID:"), 0xF501);
            Send1L(desc, Sel("setVendorID:"), 0xF001);
            Send1L(desc, Sel("setSerialNum:"), 1);
            var queue = dispatch_get_global_queue(0, 0);
            if (queue != IntPtr.Zero)
                Send1(desc, Sel("setQueue:"), queue);

            var display = Send1(Send(dispCls, Sel("alloc")), Sel("initWithDescriptor:"), desc);
            Send(desc, Sel("release"));
            if (display == IntPtr.Zero)
            {
                error = "无法创建虚拟副屏。";
                return false;
            }

            var mode = Send3(Send(modeCls, Sel("alloc")), Sel("initWithWidth:height:refreshRate:"),
                pixelWidth, pixelHeight, refreshHz);
            var settings = Send(Send(settingsCls, Sel("alloc")), Sel("init"));
            if (mode == IntPtr.Zero || settings == IntPtr.Zero)
            {
                if (mode != IntPtr.Zero) Send(mode, Sel("release"));
                if (settings != IntPtr.Zero) Send(settings, Sel("release"));
                Send(display, Sel("release"));
                error = "无法配置虚拟副屏分辨率。";
                return false;
            }

            var arrayCls = objc_getClass("NSArray");
            var modes = Send1(arrayCls, Sel("arrayWithObject:"), mode);
            Send1(settings, Sel("setModes:"), modes);
            Send1(display, Sel("applySettings:"), settings);
            Send(mode, Sel("release"));
            Send(settings, Sel("release"));

            _display = display;
            _id = (uint)SendNUInt(display, Sel("displayID"));
            if (_id == 0)
            {
                Release();
                error = "虚拟副屏已创建，但系统没有分配显示器。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Release();
            error = "创建虚拟副屏失败：" + ex.Message;
            return false;
        }
        finally
        {
            if (pool != IntPtr.Zero)
            {
                try { objc_autoreleasePoolPop(pool); } catch { /* ignore */ }
            }
        }
    }

    public void Dispose() => Release();

    private void Release()
    {
        var display = Interlocked.Exchange(ref _display, IntPtr.Zero);
        _id = 0;
        if (display == IntPtr.Zero) return;
        try { Send(display, Sel("release")); } catch { /* ignore */ }
    }

    private static IntPtr NsString(string text)
    {
        var cls = objc_getClass("NSString");
        return cls == IntPtr.Zero ? IntPtr.Zero : SendUtf8(cls, Sel("stringWithUTF8String:"), text);
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(Objc, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send1L(IntPtr receiver, IntPtr selector, nint arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send3(IntPtr receiver, IntPtr selector, nint width, nint height, double refresh);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, string utf8);

    [DllImport(System)]
    private static extern IntPtr dispatch_get_global_queue(nint identifier, nuint flags);
}
