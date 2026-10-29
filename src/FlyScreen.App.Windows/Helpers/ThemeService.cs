using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlyScreen.Native;
using Media = System.Windows.Media;
using WpfControls = System.Windows.Controls;

namespace FlyScreen.Helpers;

/// <summary>
/// 淡色 / 深色主题：替换窗口资源画刷，配合 DynamicResource 即时生效。
/// </summary>
public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    public static bool IsDark(string? theme)
        => string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? theme, FrameworkElement? root = null)
    {
        Current = IsDark(theme) ? Dark : Light;
        var dark = Current == Dark;

        root ??= Application.Current?.MainWindow;
        if (root == null) return;

        var rd = root.Resources;
        Set(rd, "Blue", dark ? "#3B82F6" : "#2563EB");
        Set(rd, "BlueSoft", dark ? "#1E3A5F" : "#DBEAFE");
        Set(rd, "BlueHover", dark ? "#243447" : "#EFF6FF");
        Set(rd, "BluePress", dark ? "#1D4ED8" : "#1D4ED8");
        Set(rd, "Line", dark ? "#334155" : "#E5E7EB");
        Set(rd, "Muted", dark ? "#94A3B8" : "#6B7280");
        Set(rd, "Text", dark ? "#E2E8F0" : "#111827");
        Set(rd, "Success", dark ? "#34D399" : "#059669");
        Set(rd, "Warn", dark ? "#FBBF24" : "#D97706");
        Set(rd, "WarnSoft", dark ? "#422006" : "#FEF3C7");
        Set(rd, "WinBg", dark ? "#0F172A" : "#F7F8FA");
        Set(rd, "CardBg", dark ? "#1E293B" : "#FFFFFF");
        Set(rd, "PaneBg", dark ? "#111827" : "#FFFFFF");
        Set(rd, "SubtleBg", dark ? "#1E293B" : "#F3F4F6");
        Set(rd, "ControlBg", dark ? "#0F172A" : "#FFFFFF");
        Set(rd, "ControlBorder", dark ? "#475569" : "#D1D5DB");
        Set(rd, "ControlHover", dark ? "#334155" : "#F9FAFB");
        Set(rd, "Placeholder", dark ? "#64748B" : "#9CA3AF");
        Set(rd, "MatrixBg", dark ? "#0B1220" : "#F8FAFC");
        Set(rd, "ListBg", dark ? "#0B1220" : "#F9FAFB");

        // 覆盖窗口级 SystemColors，避免 ListView/ScrollViewer 默认白底
        OverrideSystemColorsOn(rd, dark);

        if (root is Window win)
        {
            win.Background = BrushOf(rd, "WinBg");
            win.Foreground = BrushOf(rd, "Text");
            ApplyNativeTitleBar(win, dark);
            win.Dispatcher.BeginInvoke(() => OverrideListSystemColors(win, rd), DispatcherPriority.Loaded);
            OverrideListSystemColors(win, rd);
        }

        root.InvalidateVisual();
        root.InvalidateArrange();
    }

    /// <summary>
    /// 同步 DWM 边框色（主窗已自绘标题栏；子窗仍依赖 DWM）。
    /// Win10 首次显示前常无效，Loaded / ContentRendered / 短延迟再刷。
    /// </summary>
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

    /// <summary>
    /// ListView/ListBox 默认模板使用 SystemColors.WindowBrush，需覆盖否则深色下仍为白底。
    /// </summary>
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

    /// <summary>将当前主题应用到子窗口。</summary>
    public static void ApplyToChildWindow(Window child) => Apply(Current, child);

    private static void Set(ResourceDictionary rd, string key, string hex)
    {
        var color = (Media.Color)Media.ColorConverter.ConvertFromString(hex)!;
        // 原地改 Color，已绑定的 DynamicResource / 画刷引用才能同步变色
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
