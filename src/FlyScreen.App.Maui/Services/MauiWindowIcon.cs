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
/// MAUI-Windows 窗体图标。
/// 标题栏走自定义 AppTitleBar（默认找不存在的 Square44x44Logo.png）；
/// 此处再确保 Image 源、任务栏 AppWindow.SetIcon、exe 内嵌图标一致。
/// </summary>
public static class MauiWindowIcon
{
    private static readonly HashSet<int> Hooked = [];
    private static IntPtr _exeIcon;

    public static void Apply(Microsoft.Maui.Controls.Window window)
    {
        var id = RuntimeHelpers.GetHashCode(window);
        if (Hooked.Add(id))
        {
            window.HandlerChanged += (_, _) => Schedule(window);
            window.Created += (_, _) => Schedule(window);
        }

        Schedule(window);
    }

    private static void Schedule(Microsoft.Maui.Controls.Window window)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            TryApply(window);
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => TryApply(window));
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), () => TryApply(window));
        });
    }

    private static void TryApply(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;

            // 任务栏 / Alt-Tab：用 exe 内嵌 ApplicationIcon
            try
            {
                native.GetType().GetMethod("SetIcon", Type.EmptyTypes)?.Invoke(native, null);
            }
            catch { /* ignore */ }

            var hwnd = WindowNative.GetWindowHandle(native);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            EnsureExeIcon(appWindow);

            var ico = ResolvePath("appicon.ico", Path.Combine("Assets", "appicon.ico"));
            if (ico != null)
            {
                try { appWindow.SetIcon(ico); } catch { /* ignore */ }
            }

            // 标题栏 Image：修掉默认坏掉的 Square44x44Logo.png
            FixTitleBarImage(native);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void EnsureExeIcon(AppWindow appWindow)
    {
        if (_exeIcon != IntPtr.Zero) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        var idx = IntPtr.Zero;
        _exeIcon = ExtractAssociatedIcon(IntPtr.Zero, exe, ref idx);
        if (_exeIcon == IntPtr.Zero) return;
        try
        {
            var iconId = Microsoft.UI.Win32Interop.GetIconIdFromIcon(_exeIcon);
            appWindow.SetIcon(iconId);
        }
        catch { /* ignore */ }
    }

    private static void FixTitleBarImage(Microsoft.UI.Xaml.Window native)
    {
        if (native.Content is not DependencyObject root) return;

        var img = FindByName(root, "AppFontIcon") as Microsoft.UI.Xaml.Controls.Image;
        if (img == null) return;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string lpIconPath, ref IntPtr lpiIcon);
}
#endif
