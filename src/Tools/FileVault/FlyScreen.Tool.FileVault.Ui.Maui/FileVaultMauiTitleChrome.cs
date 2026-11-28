using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

/// <summary>
/// Maui-Win：仅自绘窗控（最小/最大）+ Tab 中间区按住拖窗。
/// 假标题栏清除由宿主 <c>MauiFileVaultWindowChrome</c> 负责，此处不再反射折叠。
/// </summary>
internal static class FileVaultMauiTitleChrome
{
    private static readonly ConditionalWeakTable<View, object> DragHooked = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct PointApi
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointApi lpPoint);

    public static void Apply(Window window, View? titleDrag)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            HookDrag(window, titleDrag);
            window.HandlerChanged -= OnHandler;
            window.Created -= OnCreated;
            window.HandlerChanged += OnHandler;
            window.Created += OnCreated;
        }
        catch
        {
            // ignore
        }

        void OnHandler(object? s, EventArgs e)
        {
            if (s is Window w)
                MainThread.BeginInvokeOnMainThread(() => HookDrag(w, titleDrag));
        }

        void OnCreated(object? s, EventArgs e)
        {
            if (s is Window w)
                MainThread.BeginInvokeOnMainThread(() => HookDrag(w, titleDrag));
        }
    }

    private static void HookDrag(Window window, View? titleDrag)
    {
        try
        {
            var native = window.Handler?.PlatformView;
            if (native is null) return;
            if (!TryGetAppWindow(native, out var appWindow, out _))
                return;
            HookHoldToDrag(titleDrag, appWindow);
        }
        catch
        {
            // ignore
        }
    }

    private static void HookHoldToDrag(View? drag, object appWindow)
    {
        if (drag is null) return;
        if (DragHooked.TryGetValue(drag, out _)) return;
        DragHooked.Add(drag, new object());

        var dragging = false;
        var startCursor = new PointApi();
        object? startPos = null;

        var recognizer = new PointerGestureRecognizer();
        recognizer.PointerPressed += (_, _) =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return;
                if (!GetCursorPos(out startCursor)) return;
                startPos = appWindow.GetType().GetProperty("Position")?.GetValue(appWindow);
                dragging = startPos is not null;
            }
            catch { dragging = false; }
        };
        recognizer.PointerMoved += (_, _) =>
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !dragging || startPos is null) return;
                if (!GetCursorPos(out var cur)) return;
                var startX = Convert.ToInt32(startPos.GetType().GetProperty("X")?.GetValue(startPos) ?? 0);
                var startY = Convert.ToInt32(startPos.GetType().GetProperty("Y")?.GetValue(startPos) ?? 0);
                var pointType = startPos.GetType();
                var point = Activator.CreateInstance(pointType)!;
                pointType.GetField("X")?.SetValue(point, startX + (cur.X - startCursor.X));
                pointType.GetField("Y")?.SetValue(point, startY + (cur.Y - startCursor.Y));
                pointType.GetProperty("X")?.SetValue(point, startX + (cur.X - startCursor.X));
                pointType.GetProperty("Y")?.SetValue(point, startY + (cur.Y - startCursor.Y));
                appWindow.GetType().GetMethod("Move", [pointType])?.Invoke(appWindow, [point]);
            }
            catch { /* ignore */ }
        };
        recognizer.PointerReleased += (_, _) => { dragging = false; startPos = null; };
        drag.GestureRecognizers.Add(recognizer);
    }

    public static void Minimize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (TryGetPresenter(window, out var op))
                op.GetType().GetMethod("Minimize", Type.EmptyTypes)?.Invoke(op, null);
        }
        catch { /* ignore */ }
    }

    public static void ToggleMaximize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!TryGetPresenter(window, out var op)) return;
            var state = op.GetType().GetProperty("State")?.GetValue(op)?.ToString();
            if (string.Equals(state, "Maximized", StringComparison.Ordinal))
                op.GetType().GetMethod("Restore", Type.EmptyTypes)?.Invoke(op, null);
            else
                op.GetType().GetMethod("Maximize", Type.EmptyTypes)?.Invoke(op, null);
        }
        catch { /* ignore */ }
    }

    public static bool IsMaximized(Window window)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!TryGetPresenter(window, out var op)) return false;
            return string.Equals(op.GetType().GetProperty("State")?.GetValue(op)?.ToString(),
                "Maximized", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPresenter(Window window, out object presenter)
    {
        presenter = null!;
        var native = window.Handler?.PlatformView;
        if (native is null || !TryGetAppWindow(native, out var app, out _))
            return false;
        return TryGetOverlappedPresenter(app, out presenter);
    }

    private static bool TryGetOverlappedPresenter(object appWindow, out object presenter)
    {
        presenter = null!;
        var p = appWindow.GetType().GetProperty("Presenter")?.GetValue(appWindow);
        if (p is null || !p.GetType().Name.Contains("Overlapped", StringComparison.Ordinal))
            return false;
        presenter = p;
        return true;
    }

    private static bool TryGetAppWindow(object native, out object appWindow, out IntPtr hwnd)
    {
        appWindow = null!;
        hwnd = IntPtr.Zero;
        try
        {
            var windowNative = Type.GetType("WinRT.Interop.WindowNative, Microsoft.Windows.SDK.NET")
                               ?? Type.GetType("WinRT.Interop.WindowNative, WinRT.Interop");
            var getHandle = windowNative?.GetMethod("GetWindowHandle", BindingFlags.Public | BindingFlags.Static);
            hwnd = getHandle is null
                ? IntPtr.Zero
                : (IntPtr)(getHandle.Invoke(null, [native]) ?? IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return false;

            var win32 = Type.GetType("Microsoft.UI.Win32Interop, Microsoft.Windows.SDK.NET")
                        ?? Type.GetType("Microsoft.UI.Win32Interop, Microsoft.WinUI");
            var id = win32?.GetMethod("GetWindowIdFromWindow", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, [hwnd]);
            if (id is null) return false;

            var appWindowType = Type.GetType("Microsoft.UI.Windowing.AppWindow, Microsoft.Windows.SDK.NET")
                                ?? Type.GetType("Microsoft.UI.Windowing.AppWindow, Microsoft.WinUI");
            var aw = appWindowType?.GetMethod("GetFromWindowId", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, [id]);
            if (aw is null) return false;
            appWindow = aw;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
