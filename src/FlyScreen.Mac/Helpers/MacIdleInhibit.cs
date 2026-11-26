using System.Runtime.InteropServices;
using FlyScreen.Mac.Permissions;

namespace FlyScreen.Mac.Helpers;

/// <summary>macOS：IOPM 断言阻止空闲熄屏；非 macOS（Win 调试宿主）走 SetThreadExecutionState。</summary>
public static class MacIdleInhibit
{
    private const string Reason = "FlyScreen block screensaver";
    private const string TypeDisplay = "PreventUserIdleDisplaySleep";
    private const string TypeSystem = "PreventUserIdleSystemSleep";
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint AssertionLevelOn = 255;

    private static readonly object Gate = new();
    private static uint _displayAssertion;
    private static uint _systemAssertion;
    private static bool _active;
    private static Timer? _timer;

    public static void SetActive(bool active)
    {
        lock (Gate)
        {
            if (AccessibilityPermission.IsMacOS)
                SetMac(active);
            else if (OperatingSystem.IsWindows())
                SetWindows(active);
            else
                _active = active;
        }
    }

    private static void SetMac(bool active)
    {
        if (active)
        {
            _active = true;
            EnsureMacAssertions();
            _timer ??= new Timer(_ =>
            {
                lock (Gate)
                {
                    if (_active) EnsureMacAssertions();
                }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        else
        {
            _active = false;
            try { _timer?.Dispose(); } catch { /* ignore */ }
            _timer = null;
            ReleaseMacAssertions();
        }
    }

    private static void EnsureMacAssertions()
    {
        try
        {
            if (_displayAssertion == 0)
                TryCreate(TypeDisplay, out _displayAssertion);
            if (_systemAssertion == 0)
                TryCreate(TypeSystem, out _systemAssertion);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryCreate(string typeName, out uint id)
    {
        id = 0;
        var type = CfString(typeName);
        var name = CfString(Reason);
        try
        {
            if (type == IntPtr.Zero || name == IntPtr.Zero) return;
            if (IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out var created) == 0)
                id = created;
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    private static void ReleaseMacAssertions()
    {
        try
        {
            if (_displayAssertion != 0)
            {
                IOPMAssertionRelease(_displayAssertion);
                _displayAssertion = 0;
            }

            if (_systemAssertion != 0)
            {
                IOPMAssertionRelease(_systemAssertion);
                _systemAssertion = 0;
            }
        }
        catch
        {
            _displayAssertion = 0;
            _systemAssertion = 0;
        }
    }

    private static void SetWindows(bool active)
    {
        _active = active;
        try
        {
            if (active)
                SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
            else
                SetThreadExecutionState(EsContinuous);
        }
        catch
        {
            // ignore
        }
    }

    private static IntPtr CfString(string s)
        => CFStringCreateWithCString(IntPtr.Zero, s, 0x08000100);

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(
        IntPtr assertionType, uint assertionLevel, IntPtr assertionName, out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
