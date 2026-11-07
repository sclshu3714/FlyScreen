using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using WinForms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace FlyScreen.Tray;

/// <summary>
/// 托盘右键菜单（实现方式对齐 PrintBridge TrayFlyout：透明窗 + 圆角卡片 + DropShadow；内容仍为玄鸟菜单项）。
/// </summary>
public sealed class TrayContextMenuWindow : Window
{
    private static TrayContextMenuWindow? _open;

    private readonly Action _onOpen;
    private readonly Action _onReconnect;
    private readonly Action _onProject;
    private readonly ProjectionUiPhase _projectPhase;
    private readonly Action _onAbout;
    private readonly Action _onExit;
    private bool _closing;

    public TrayContextMenuWindow(
        Action onOpen,
        Action onReconnect,
        Action onProject,
        ProjectionUiPhase projectPhase,
        Action onAbout,
        Action onExit)
    {
        _onOpen = onOpen;
        _onReconnect = onReconnect;
        _onProject = onProject;
        _projectPhase = projectPhase;
        _onAbout = onAbout;
        _onExit = onExit;

        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = true;
        SnapsToDevicePixels = true;

        Content = BuildChrome();
        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseMenu();
            }
        };
    }

    public static void ShowNearCursor(
        Action onOpen,
        Action onReconnect,
        Action onProject,
        ProjectionUiPhase projectPhase,
        Action onAbout,
        Action onExit)
    {
        try { _open?.CloseMenu(); } catch { /* ignore */ }

        var w = new TrayContextMenuWindow(onOpen, onReconnect, onProject, projectPhase, onAbout, onExit);
        _open = w;
        // 先在光标处就位再显示。直接 Show 会先画在 (0,0)，再跳到鼠标，看起来从左上角闪过来。
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Opacity = 0;
        if (w.Content is FrameworkElement root)
        {
            root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            PlaceNearCursor(w, root.DesiredSize.Width, root.DesiredSize.Height);
        }

        if (!w.IsVisible)
            w.Show();
        w.UpdateLayout();
        PlaceNearCursor(w, w.ActualWidth, w.ActualHeight);
        w.Opacity = 1;
        BringAboveShell(w);
        w.Activate();
        try { w.Focus(); } catch { /* ignore */ }
    }

    private FrameworkElement BuildChrome()
    {
        var card = new Border
        {
            Margin = new Thickness(14),
            Background = new SolidColorBrush(WpfColor.FromRgb(0xFB, 0xFD, 0xFF)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xC9, 0xD7, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinWidth = 188,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.28,
                Color = WpfColor.FromRgb(0x14, 0x23, 0x3A),
                RenderingBias = RenderingBias.Quality
            }
        };

        var stack = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
        stack.Children.Add(BuildItem(Loc.T("Tray.Open"), _onOpen));
        stack.Children.Add(BuildItem(Loc.T("Tray.Reconnect"), _onReconnect));
        stack.Children.Add(BuildProjectItem());
        stack.Children.Add(BuildItem(Loc.T("Tray.About"), _onAbout));
        stack.Children.Add(BuildSeparator());
        stack.Children.Add(BuildItem(Loc.T("Tray.Exit"), _onExit));
        card.Child = stack;
        return card;
    }

    private static FrameworkElement BuildSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(WpfColor.FromRgb(0xD5, 0xE0, 0xEC)),
            Margin = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = WpfHAlign.Stretch,
            SnapsToDevicePixels = true
        };
    }

    private FrameworkElement BuildProjectItem()
    {
        var hoverBg = new SolidColorBrush(WpfColor.FromRgb(0xE8, 0xF2, 0xFF));
        var hoverBorder = new SolidColorBrush(WpfColor.FromRgb(0xA9, 0xC3, 0xDE));
        var accentBrush = new SolidColorBrush(WpfColor.FromRgb(0x25, 0x63, 0xEB));
        var textBrush = new SolidColorBrush(WpfColor.FromRgb(0x11, 0x18, 0x27));
        var emphasizeBrush = new SolidColorBrush(WpfColor.FromRgb(0x25, 0x63, 0xEB));

        var accent = new Border
        {
            Width = 3,
            Height = 20,
            CornerRadius = new CornerRadius(1.5),
            Background = WpfBrushes.Transparent,
            HorizontalAlignment = WpfHAlign.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-8, 0, 0, 0)
        };
        var icon = new TextBlock
        {
            FontSize = 13,
            FontFamily = new WpfFontFamily("Segoe MDL2 Assets"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var text = new TextBlock
        {
            FontSize = 13,
            FontFamily = new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };

        void ApplyChrome(bool hovered)
        {
            var chrome = ProjectionUi.Tray(_projectPhase, hovered);
            icon.Text = chrome.Mdl2Glyph;
            text.Text = chrome.Label;
            var fg = chrome.Emphasized ? emphasizeBrush : textBrush;
            icon.Foreground = fg;
            text.Foreground = fg;
        }

        ApplyChrome(false);

        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(text);

        var inner = new Grid();
        inner.Children.Add(accent);
        inner.Children.Add(content);

        var row = new Border
        {
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 0, 14, 0),
            Height = 36,
            Margin = new Thickness(4, 1, 4, 1),
            Cursor = WpfCursors.Hand,
            Child = inner,
            SnapsToDevicePixels = true
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = hoverBg;
            row.BorderBrush = hoverBorder;
            accent.Background = accentBrush;
            ApplyChrome(true);
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = WpfBrushes.Transparent;
            row.BorderBrush = WpfBrushes.Transparent;
            accent.Background = WpfBrushes.Transparent;
            ApplyChrome(false);
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseMenu();
            try { _onProject(); } catch { /* ignore */ }
        };
        return row;
    }

    private FrameworkElement BuildItem(string label, Action action)
    {
        var hoverBg = new SolidColorBrush(WpfColor.FromRgb(0xE8, 0xF2, 0xFF));
        var hoverBorder = new SolidColorBrush(WpfColor.FromRgb(0xA9, 0xC3, 0xDE));
        var accentBrush = new SolidColorBrush(WpfColor.FromRgb(0x25, 0x63, 0xEB));

        var accent = new Border
        {
            Width = 3,
            Height = 20,
            CornerRadius = new CornerRadius(1.5),
            Background = WpfBrushes.Transparent,
            HorizontalAlignment = WpfHAlign.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-8, 0, 0, 0)
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontFamily = new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x11, 0x18, 0x27)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var inner = new Grid();
        inner.Children.Add(accent);
        inner.Children.Add(text);

        var row = new Border
        {
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 0, 14, 0),
            Height = 36,
            Margin = new Thickness(4, 1, 4, 1),
            Cursor = WpfCursors.Hand,
            Child = inner,
            SnapsToDevicePixels = true
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = hoverBg;
            row.BorderBrush = hoverBorder;
            accent.Background = accentBrush;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = WpfBrushes.Transparent;
            row.BorderBrush = WpfBrushes.Transparent;
            accent.Background = WpfBrushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseMenu();
            try { action(); } catch { /* ignore */ }
        };
        return row;
    }

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        if (ReferenceEquals(_open, this))
            _open = null;
        try { Close(); } catch { /* ignore */ }
    }

    private static void PlaceNearCursor(Window w, double width, double height)
    {
        if (width < 1) width = w.ActualWidth;
        if (height < 1) height = w.ActualHeight;
        if (width < 1 || height < 1) return;

        var mouse = WinForms.Control.MousePosition;
        double scaleX = 1d, scaleY = 1d;
        var source = PresentationSource.FromVisual(w);
        if (source?.CompositionTarget != null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }
        else
        {
            var dpi = GetDpiForPoint(mouse.X, mouse.Y);
            if (dpi > 0)
                scaleX = scaleY = dpi / 96d;
        }
        var x = mouse.X / scaleX - width + 28;
        var y = mouse.Y / scaleY - height + 8;

        var work = SystemParameters.WorkArea;
        if (x < work.Left + 4) x = work.Left + 4;
        if (y < work.Top + 4) y = work.Top + 4;
        if (x + width > work.Right - 4) x = work.Right - width - 4;
        if (y + height > work.Bottom - 4) y = work.Bottom - height - 4;

        w.Left = x;
        w.Top = y;
    }

    private static void BringAboveShell(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;
            const int hwndTopmost = -1;
            const uint swpNomove = 0x0002;
            const uint swpNosize = 0x0001;
            const uint swpShowwindow = 0x0040;
            const uint swpNoactivate = 0x0010;
            SetWindowPos(hwnd, (IntPtr)hwndTopmost, 0, 0, 0, 0, swpNomove | swpNosize | swpShowwindow);
            SetForegroundWindow(hwnd);
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    SetWindowPos(hwnd, (IntPtr)hwndTopmost, 0, 0, 0, 0, swpNomove | swpNosize | swpNoactivate);
                }
                catch { /* ignore */ }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch { /* ignore */ }
    }

    private static uint GetDpiForPoint(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
                && dpiX > 0)
                return dpiX;
        }
        catch
        {
            /* 96dpi */
        }

        return 96;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
