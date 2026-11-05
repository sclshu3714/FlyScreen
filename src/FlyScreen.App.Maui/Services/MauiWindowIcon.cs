#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// MAUI-Windows 窗体图标与任务管理器显示名。
/// <para>
/// WinUI 主窗默认是 ANSI 窗（<c>IsWindowUnicode=false</c>），同进程写中文标题会截成「玄」。
/// 在 HWND 就绪时用 <c>SetWindowLongPtrW</c> 挂一次 Unicode 子类，标题消息走
/// <c>DefWindowProcW</c>，再写一次「玄鸟」即可——无需定时器反复刷。
/// </para>
/// </summary>
public static class MauiWindowIcon
{
    private static string AppDisplayName => FlyScreen.WinApi.WindowsAppUserModel.MainDisplayName;

    private const int GwlpWndProc = -4;
    private const uint WmSetText = 0x000C;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;

    private static readonly HashSet<int> HookedWindows = [];
    private static readonly HashSet<nint> SubclassedHwnds = [];
    private static readonly Dictionary<nint, IntPtr> OldWndProcs = [];
    private static WndProc? _subclassKeepAlive;
    private static IntPtr _smallIcon;
    private static IntPtr _bigIcon;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void Apply(Microsoft.Maui.Controls.Window window)
    {
        var id = RuntimeHelpers.GetHashCode(window);
        if (HookedWindows.Add(id))
        {
            // HWND 可能尚未创建：等 Handler/Created，各试一次即可
            window.HandlerChanged += (_, _) => TryApply(window);
            window.Created += (_, _) => TryApply(window);
        }

        window.Title = AppDisplayName;
        TryApply(window);
    }

    private static void TryApply(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            window.Title = AppDisplayName;

            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;

            var hwnd = WindowNative.GetWindowHandle(native);
            if (hwnd == IntPtr.Zero) return;

            EnsureUnicodeSubclass(hwnd);
            _ = SetWindowTextW(hwnd, AppDisplayName);
            // 任务栏右键顶栏显示「玄鸟」（进程名列仍为 FlyScreen.App.Maui.exe）
            FlyScreen.WinApi.WindowsAppUserModel.ApplyToWindow(
                hwnd,
                FlyScreen.WinApi.WindowsAppUserModel.MainAppId,
                FlyScreen.WinApi.WindowsAppUserModel.MainDisplayName,
                Path.Combine("Assets", "appicon.ico"),
                Path.Combine("Resources", "AppIcon", "appicon.ico"),
                "appicon.ico");

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            var ico = ResolvePath(
                Path.Combine("Assets", "appicon.ico"),
                Path.Combine("Platforms", "Windows", "Assets", "appicon.ico"),
                Path.Combine("Resources", "AppIcon", "appicon.ico"),
                "appicon.ico");
            if (ico != null)
            {
                try { appWindow.SetIcon(ico); } catch { /* ignore */ }
                ApplyWin32Icons(hwnd, ico);
            }

            FixTitleBarImage(native);
            FixTitleBarText(native);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>仅安装一次：ANSI → Unicode 子类；标题读写走 DefWindowProcW。</summary>
    private static void EnsureUnicodeSubclass(IntPtr hwnd)
    {
        if (!SubclassedHwnds.Add(hwnd))
            return;
        if (IsWindowUnicode(hwnd))
            return;

        _subclassKeepAlive ??= UnicodeSubclassProc;
        var procPtr = Marshal.GetFunctionPointerForDelegate(_subclassKeepAlive);
        Marshal.SetLastPInvokeError(0);
        var old = SetWindowLongPtrW(hwnd, GwlpWndProc, procPtr);
        if (old == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            SubclassedHwnds.Remove(hwnd);
            return;
        }

        OldWndProcs[hwnd] = old;
    }

    private static IntPtr UnicodeSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 标题必须走 Unicode；回 ANSI 旧过程会再次截断中文
        if (msg is WmSetText or WmGetText or WmGetTextLength)
            return DefWindowProcW(hWnd, msg, wParam, lParam);

        if (!OldWndProcs.TryGetValue(hWnd, out var old) || old == IntPtr.Zero)
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        return CallWindowProcW(old, hWnd, msg, wParam, lParam);
    }

    private static void ApplyWin32Icons(IntPtr hwnd, string icoPath)
    {
        if (_smallIcon == IntPtr.Zero)
            _smallIcon = LoadImage(IntPtr.Zero, icoPath, ImageIcon, 16, 16, LrLoadFromFile);
        if (_bigIcon == IntPtr.Zero)
            _bigIcon = LoadImage(IntPtr.Zero, icoPath, ImageIcon, 32, 32, LrLoadFromFile);

        if (_smallIcon != IntPtr.Zero)
            _ = SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, _smallIcon);
        if (_bigIcon != IntPtr.Zero)
            _ = SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, _bigIcon);
    }

    private static void FixTitleBarImage(Microsoft.UI.Xaml.Window native)
    {
        if (native.Content is not DependencyObject root) return;
        if (FindByName(root, "AppFontIcon") is not Microsoft.UI.Xaml.Controls.Image img)
            return;

        var png = ResolvePath(
            Path.Combine("Assets", "appiconLogo.png"),
            "appiconLogo.scale-100.png",
            "Square44x44Logo.png",
            "appicon.png");
        if (png == null) return;

        try
        {
            img.Width = 16;
            img.Height = 16;
            img.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            img.Source = new BitmapImage(new Uri(png, UriKind.Absolute));
        }
        catch { /* ignore */ }
    }

    private static void FixTitleBarText(Microsoft.UI.Xaml.Window native)
    {
        if (native.Content is not DependencyObject root) return;
        if (FindByName(root, "AppTitle") is not Microsoft.UI.Xaml.Controls.TextBlock title)
            return;
        try { title.Text = AppDisplayName; } catch { /* ignore */ }
    }

    private static DependencyObject? FindByName(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement fe && fe.Name == name)
            return parent;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var found = FindByName(VisualTreeHelper.GetChild(parent, i), name);
            if (found != null) return found;
        }

        return null;
    }

    private static string? ResolvePath(params string[] relatives)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var r in relatives)
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, r));
            if (File.Exists(full)) return full;
        }

        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowUnicode(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW_x64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLongW_x86(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtrW_x64(hWnd, nIndex, dwNewLong)
            : SetWindowLongW_x86(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(
        IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
}
#endif
