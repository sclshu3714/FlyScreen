using System.Runtime.InteropServices;
using FlyScreen.Mac.Permissions;

namespace FlyScreen.Mac.LockInput;

/// <summary>通过 CGSession 字典轮询本机是否锁屏。</summary>
public static class MacLockScreenSession
{
    private static int _started;
    private static volatile bool _locked;
    private static Timer? _timer;

    public static bool IsLocked => _locked;

    public static event Action<bool>? Changed;

    public static void EnsureStarted()
    {
        if (!AccessibilityPermission.IsMacOS) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public static void Stop()
    {
        try { _timer?.Dispose(); } catch { /* ignore */ }
        _timer = null;
        Interlocked.Exchange(ref _started, 0);
    }

    private static void Poll()
    {
        var locked = ReadLocked();
        if (_locked == locked) return;
        _locked = locked;
        try { Changed?.Invoke(locked); } catch { /* ignore */ }
    }

    private static bool ReadLocked()
    {
        try
        {
            var dict = CGSessionCopyCurrentDictionary();
            if (dict == IntPtr.Zero) return false;
            try
            {
                var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", 0x08000100);
                if (key == IntPtr.Zero) return false;
                try
                {
                    var val = CFDictionaryGetValue(dict, key);
                    if (val == IntPtr.Zero) return false;
                    return CFBooleanGetValue(val);
                }
                finally
                {
                    CFRelease(key);
                }
            }
            finally
            {
                CFRelease(dict);
            }
        }
        catch
        {
            return false;
        }
    }

    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(ApplicationServices)]
    private static extern IntPtr CGSessionCopyCurrentDictionary();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(IntPtr boolean);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
