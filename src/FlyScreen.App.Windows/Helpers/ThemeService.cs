using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyScreen.Native;
using FlyScreen.Services;
using FlyScreen.Ui.Theme;
using Media = System.Windows.Media;
using WpfControls = System.Windows.Controls;
using WpfImage = System.Windows.Controls.Image;
using WpfBorder = System.Windows.Controls.Border;

namespace FlyScreen.Helpers;

/// <summary>
/// 界面主题：按 <see cref="ThemePalette"/> 已注册主题覆写资源画刷，配合 DynamicResource 即时生效。
/// </summary>
public static class ThemeService
{
    public const string Light = ThemePalette.LightName;
    public const string Dark = ThemePalette.DarkName;
    public const string Custom = ThemePalette.CustomName;

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme) => ThemePalette.IsDark(theme);

    public static void Apply(string? theme, FrameworkElement? root = null, string? wallpaperPath = null)
    {
        var info = ThemePalette.Resolve(theme);
        Current = info.Id;
        var dark = info.IsDark;
        var map = ThemePalette.ToResourceMap(info.ResolveColors());

        root ??= Application.Current?.MainWindow;
        if (root == null) return;

        var rd = root.Resources;
        foreach (var (key, hex) in map)
            Set(rd, key, hex);
        // 主窗 Resources 内若还有 Merged 色板副本，一并改色
        SyncDictionary(rd, map);

        OverrideSystemColorsOn(rd, dark);
        SyncAppThemeBrushes(map);
        // 工具面板等会再合并一份 WpfTheme；只改 App 顶层会导致滚动条仍用浅色令牌
        SyncVisualTreeResources(root, map);

        if (root is Window win)
        {
            // 自定义无壁纸时用实心浅色衬底，避免半透明色板衬系统黑底
            if (info.SupportsWallpaper && UiWallpaperStore.ResolveExisting(wallpaperPath) == null)
                win.Background = new Media.SolidColorBrush(Media.Color.FromRgb(0xF7, 0xF8, 0xFA));
            else if (info.SupportsWallpaper)
                win.Background = Media.Brushes.Transparent;
            else
                win.Background = BrushOf(rd, "WinBg");
            win.Foreground = BrushOf(rd, "Text");
            ApplyNativeTitleBar(win, dark);
            win.Dispatcher.BeginInvoke(() => OverrideListSystemColors(win, rd), DispatcherPriority.Loaded);
            OverrideListSystemColors(win, rd);
        }

        ApplyWallpaper(root, info.SupportsWallpaper, wallpaperPath, rd);

        root.InvalidateVisual();
        root.InvalidateArrange();
    }

    /// <summary>仅 SupportsWallpaper 主题铺图；淡色/深色必清壁纸层。标题/外框跟 PaneBg/WinBg（可含 Alpha）。</summary>
    private static void ApplyWallpaper(
        FrameworkElement root, bool supportsWallpaper, string? wallpaperPath, ResourceDictionary rd)
    {
        var image = FindName<WpfImage>(root, "WallpaperImage");
        var scrim = FindName<WpfBorder>(root, "WallpaperScrim");
        var outer = FindName<WpfBorder>(root, "WindowOuterBorder");
        var title = FindName<WpfBorder>(root, "AppTitleBar");
        var winBg = BrushOf(rd, "WinBg");
        var paneBg = BrushOf(rd, "PaneBg");
        var path = supportsWallpaper ? UiWallpaperStore.ResolveExisting(wallpaperPath) : null;

        if (image == null && scrim == null) return;

        if (path != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                if (image != null)
                {
                    image.Source = bmp;
                    image.Visibility = Visibility.Visible;
                }
                if (scrim != null) scrim.Visibility = Visibility.Visible;
                // 外框透明让壁纸铺满；标题用半透明 PaneBg，不再盖实心 WinBg
                if (outer != null) outer.Background = Media.Brushes.Transparent;
                if (title != null) title.Background = paneBg;
            }
            catch
            {
                ClearWallpaperVisual(image, scrim, outer, title, winBg);
            }
        }
        else
        {
            ClearWallpaperVisual(image, scrim, outer, title, winBg);
            // 自定义无图：外框/标题仍用半透明色板（下有浅色窗衬底）
            if (supportsWallpaper)
            {
                if (outer != null) outer.Background = winBg;
                if (title != null) title.Background = paneBg;
            }
        }
    }

    private static void ClearWallpaperVisual(
        WpfImage? image, WpfBorder? scrim, WpfBorder? outer, WpfBorder? title, Media.Brush solidWinBg)
    {
        if (image != null)
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }
        if (scrim != null) scrim.Visibility = Visibility.Collapsed;
        if (outer != null) outer.Background = solidWinBg;
        if (title != null) title.Background = solidWinBg;
    }

    private static T? FindName<T>(FrameworkElement root, string name) where T : class
    {
        if (root.FindName(name) is T typed) return typed;
        if (root is Window { Content: FrameworkElement content } && content.FindName(name) is T nested)
            return nested;
        return null;
    }

    /// <summary>工具工作区等延迟加载的子树：同步其自带 WpfTheme 色板（滚动条 ScrollThumb* 等）。</summary>
    public static void SyncSubtree(FrameworkElement? root)
    {
        if (root is null) return;
        var map = ThemePalette.ToResourceMap(ThemePalette.Resolve(Current).ResolveColors());
        SyncVisualTreeResources(root, map);
        if (Application.Current?.MainWindow is { } main)
            OverrideListSystemColors(root, main.Resources);
        root.InvalidateVisual();
    }

    private static void ApplyNativeTitleBar(Window window, bool dark)
    {
        void Apply()
        {
            try
            {
                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero)
                    hwnd = helper.EnsureHandle();
                NativeMethods.ApplyTitleBarTheme(hwnd, dark);
            }
            catch
            {
                // 旧系统无 DWM 属性时忽略
            }
        }

        Apply();
        window.Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
        window.Dispatcher.BeginInvoke(Apply, DispatcherPriority.ContextIdle);
        window.Dispatcher.BeginInvoke(Apply, DispatcherPriority.ApplicationIdle);
        if (!window.IsLoaded)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                window.Loaded -= OnLoaded;
                Apply();
                window.Dispatcher.BeginInvoke(Apply, DispatcherPriority.ContextIdle);
            }

            window.Loaded += OnLoaded;
        }

        void OnRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnRendered;
            Apply();
        }

        window.ContentRendered += OnRendered;
    }

    private static void OverrideSystemColorsOn(ResourceDictionary rd, bool dark)
    {
        var windowBg = BrushOf(rd, "ListBg");
        var text = BrushOf(rd, "Text");
        var control = BrushOf(rd, "CardBg");
        var highlight = BrushOf(rd, "BlueSoft");
        rd[System.Windows.SystemColors.WindowBrushKey] = windowBg;
        rd[System.Windows.SystemColors.WindowTextBrushKey] = text;
        rd[System.Windows.SystemColors.ControlBrushKey] = control;
        rd[System.Windows.SystemColors.ControlTextBrushKey] = text;
        rd[System.Windows.SystemColors.HighlightBrushKey] = highlight;
        rd[System.Windows.SystemColors.HighlightTextBrushKey] = text;
        rd[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] = highlight;
        _ = dark;
    }

    private static void OverrideListSystemColors(DependencyObject root, ResourceDictionary theme)
    {
        var windowBg = BrushOf(theme, "ListBg");
        var text = BrushOf(theme, "Text");
        var highlight = BrushOf(theme, "BlueSoft");
        var control = BrushOf(theme, "CardBg");

        void Patch(WpfControls.Control fe)
        {
            fe.Resources[System.Windows.SystemColors.WindowBrushKey] = windowBg;
            fe.Resources[System.Windows.SystemColors.WindowTextBrushKey] = text;
            fe.Resources[System.Windows.SystemColors.ControlBrushKey] = control;
            fe.Resources[System.Windows.SystemColors.ControlTextBrushKey] = text;
            fe.Resources[System.Windows.SystemColors.HighlightBrushKey] = highlight;
            fe.Resources[System.Windows.SystemColors.HighlightTextBrushKey] = text;
            fe.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] = highlight;
            fe.Background = windowBg;
            fe.Foreground = text;
        }

        foreach (var lv in FindVisualChildren<WpfControls.ListView>(root))
            Patch(lv);
        foreach (var lb in FindVisualChildren<WpfControls.ListBox>(root))
            Patch(lb);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        var count = Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    public static void ApplyToChildWindow(Window child) => Apply(Current, child);

    private static void SyncAppThemeBrushes(IReadOnlyDictionary<string, string> map)
    {
        var app = Application.Current?.Resources;
        if (app == null) return;
        foreach (var (key, hex) in map)
            Set(app, key, hex);
        SyncDictionary(app, map);
    }

    private static void SyncVisualTreeResources(DependencyObject root, IReadOnlyDictionary<string, string> map)
    {
        if (root is FrameworkElement { Resources: { } rd } && HasThemeKeys(rd, map))
            SyncDictionary(rd, map);

        var count = Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            SyncVisualTreeResources(Media.VisualTreeHelper.GetChild(root, i), map);
    }

    private static bool HasThemeKeys(ResourceDictionary rd, IReadOnlyDictionary<string, string> map)
    {
        foreach (var merged in rd.MergedDictionaries)
        {
            if (merged is not null && HasThemeKeys(merged, map))
                return true;
        }

        foreach (var key in map.Keys)
        {
            if (rd.Contains(key))
                return true;
        }

        return false;
    }

    /// <summary>只改写字典里已有的主题键（含 MergedDictionaries），避免工具侧残留浅色 ScrollThumb。</summary>
    private static void SyncDictionary(ResourceDictionary rd, IReadOnlyDictionary<string, string> map)
    {
        foreach (var merged in rd.MergedDictionaries)
        {
            if (merged is not null)
                SyncDictionary(merged, map);
        }

        foreach (var (key, hex) in map)
        {
            if (!rd.Contains(key)) continue;
            Set(rd, key, hex);
        }
    }

    private static void Set(ResourceDictionary rd, string key, string hex)
    {
        var color = (Media.Color)Media.ColorConverter.ConvertFromString(hex)!;
        if (rd[key] is Media.SolidColorBrush brush)
        {
            if (brush.IsFrozen)
                brush = brush.Clone();
            brush.Color = color;
            rd[key] = brush;
            return;
        }

        rd[key] = new Media.SolidColorBrush(color);
    }

    private static Media.Brush BrushOf(ResourceDictionary rd, string key)
        => rd[key] as Media.Brush ?? Media.Brushes.Transparent;
}
