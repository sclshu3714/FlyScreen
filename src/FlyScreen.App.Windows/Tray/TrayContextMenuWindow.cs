using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
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
    private readonly IReadOnlyList<(string Label, Action Action)> _switchMachines;
    private readonly string _disableLabel;
    private readonly Action _onToggleDisable;
    private readonly IReadOnlyList<(string Label, Action Action, ImageSource? Icon)> _toolShortcuts;
    private Popup? _switchPopup;
    private Popup? _toolsPopup;
    private DispatcherTimer? _switchCloseTimer;
    private DispatcherTimer? _toolsCloseTimer;
    private Border? _switchParentRow;
    private Border? _switchParentAccent;
    private Border? _toolsParentRow;
    private Border? _toolsParentAccent;
    private bool _closing;

    public TrayContextMenuWindow(
        Action onOpen,
        Action onReconnect,
        Action onProject,
        ProjectionUiPhase projectPhase,
        Action onAbout,
        Action onExit,
        IReadOnlyList<(string Label, Action Action)>? switchMachines = null,
        string? disableLabel = null,
        Action? onToggleDisable = null,
        IReadOnlyList<(string Label, Action Action, ImageSource? Icon)>? toolShortcuts = null)
    {
        _onOpen = onOpen;
        _onReconnect = onReconnect;
        _onProject = onProject;
        _projectPhase = projectPhase;
        _onAbout = onAbout;
        _onExit = onExit;
        _switchMachines = switchMachines ?? Array.Empty<(string, Action)>();
        _disableLabel = disableLabel ?? Loc.T("Hot.Off");
        _onToggleDisable = onToggleDisable ?? (() => { });
        _toolShortcuts = toolShortcuts ?? Array.Empty<(string, Action, ImageSource?)>();

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
        Action onExit,
        IReadOnlyList<(string Label, Action Action)>? switchMachines = null,
        string? disableLabel = null,
        Action? onToggleDisable = null,
        IReadOnlyList<(string Label, Action Action, ImageSource? Icon)>? toolShortcuts = null)
    {
        try { _open?.CloseMenu(); } catch { /* ignore */ }

        var w = new TrayContextMenuWindow(
            onOpen, onReconnect, onProject, projectPhase, onAbout, onExit,
            switchMachines, disableLabel, onToggleDisable, toolShortcuts);
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
        stack.Children.Add(BuildSwitchFlyoutItem());
        stack.Children.Add(BuildToolsFlyoutItem());
        stack.Children.Add(BuildSeparator());
        stack.Children.Add(BuildItem(Loc.T("Tray.Exit"), _onExit));
        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildSwitchFlyoutItem()
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
            Text = Loc.T("Tray.Switch"),
            FontSize = 13,
            FontFamily = new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x11, 0x18, 0x27)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHAlign.Left
        };
        var chevron = new TextBlock
        {
            Text = "\uE76C", // ChevronRight
            FontSize = 12,
            FontFamily = new WpfFontFamily("Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x7C, 0x8F)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHAlign.Right
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(chevron, 1);
        content.Children.Add(text);
        content.Children.Add(chevron);

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

        var subStack = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
        foreach (var (label, action) in _switchMachines)
            subStack.Children.Add(BuildSubItem(label, action));
        if (_switchMachines.Count > 0)
            subStack.Children.Add(BuildSeparator());
        subStack.Children.Add(BuildSubItem(_disableLabel, _onToggleDisable));

        var subCard = new Border
        {
            Margin = new Thickness(4),
            Background = new SolidColorBrush(WpfColor.FromRgb(0xFB, 0xFD, 0xFF)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xC9, 0xD7, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinWidth = 168,
            Child = subStack,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.24,
                Color = WpfColor.FromRgb(0x14, 0x23, 0x3A),
                RenderingBias = RenderingBias.Quality
            }
        };

        _switchPopup = new Popup
        {
            PlacementTarget = row,
            Placement = PlacementMode.Right,
            HorizontalOffset = 2,
            VerticalOffset = -10,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Child = subCard
        };

        _switchParentRow = row;
        _switchParentAccent = accent;

        void OpenSwitch()
        {
            CloseToolsFlyout();
            CancelSwitchClose();
            if (_switchPopup != null)
                _switchPopup.IsOpen = true;
            row.Background = hoverBg;
            row.BorderBrush = hoverBorder;
            accent.Background = accentBrush;
        }

        row.MouseEnter += (_, _) => OpenSwitch();
        row.MouseLeave += (_, _) => ScheduleSwitchClose();
        subCard.MouseEnter += (_, _) => CancelSwitchClose();
        subCard.MouseLeave += (_, _) => ScheduleSwitchClose();
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenSwitch();
        };
        return row;
    }

    private FrameworkElement BuildToolsFlyoutItem()
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
            Text = Loc.T("Tray.Tools"),
            FontSize = 13,
            FontFamily = new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x11, 0x18, 0x27)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHAlign.Left
        };
        var chevron = new TextBlock
        {
            Text = "\uE76C",
            FontSize = 12,
            FontFamily = new WpfFontFamily("Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x7C, 0x8F)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHAlign.Right
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(chevron, 1);
        content.Children.Add(text);
        content.Children.Add(chevron);

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

        var subStack = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
        if (_toolShortcuts.Count == 0)
        {
            subStack.Children.Add(BuildSubItem(TrayToolsMenu.EmptyLabel, () => { }, forTools: true));
        }
        else
        {
            foreach (var (label, action, icon) in _toolShortcuts)
                subStack.Children.Add(BuildSubItem(label, action, forTools: true, icon: icon));
        }

        var subCard = new Border
        {
            Margin = new Thickness(4),
            Background = new SolidColorBrush(WpfColor.FromRgb(0xFB, 0xFD, 0xFF)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xC9, 0xD7, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinWidth = 168,
            Child = subStack,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.24,
                Color = WpfColor.FromRgb(0x14, 0x23, 0x3A),
                RenderingBias = RenderingBias.Quality
            }
        };

        _toolsPopup = new Popup
        {
            PlacementTarget = row,
            Placement = PlacementMode.Right,
            HorizontalOffset = 2,
            VerticalOffset = -10,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Child = subCard
        };

        _toolsParentRow = row;
        _toolsParentAccent = accent;

        void OpenTools()
        {
            CloseSwitchFlyout();
            CancelToolsClose();
            if (_toolsPopup != null)
                _toolsPopup.IsOpen = true;
            row.Background = hoverBg;
            row.BorderBrush = hoverBorder;
            accent.Background = accentBrush;
        }

        row.MouseEnter += (_, _) => OpenTools();
        row.MouseLeave += (_, _) => ScheduleToolsClose();
        subCard.MouseEnter += (_, _) => CancelToolsClose();
        subCard.MouseLeave += (_, _) => ScheduleToolsClose();
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenTools();
        };
        return row;
    }

    private void CancelSwitchClose()
    {
        if (_switchCloseTimer == null) return;
        _switchCloseTimer.Stop();
    }

    private void ScheduleSwitchClose()
    {
        _switchCloseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _switchCloseTimer.Tick -= SwitchCloseTick;
        _switchCloseTimer.Tick += SwitchCloseTick;
        _switchCloseTimer.Stop();
        _switchCloseTimer.Start();
    }

    private void SwitchCloseTick(object? sender, EventArgs e)
        => CloseSwitchFlyout();

    private void CloseSwitchFlyout()
    {
        CancelSwitchClose();
        if (_switchPopup != null)
            _switchPopup.IsOpen = false;
        if (_switchParentRow != null)
        {
            _switchParentRow.Background = WpfBrushes.Transparent;
            _switchParentRow.BorderBrush = WpfBrushes.Transparent;
        }

        if (_switchParentAccent != null)
            _switchParentAccent.Background = WpfBrushes.Transparent;
    }

    private void CancelToolsClose()
    {
        if (_toolsCloseTimer == null) return;
        _toolsCloseTimer.Stop();
    }

    private void ScheduleToolsClose()
    {
        _toolsCloseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _toolsCloseTimer.Tick -= ToolsCloseTick;
        _toolsCloseTimer.Tick += ToolsCloseTick;
        _toolsCloseTimer.Stop();
        _toolsCloseTimer.Start();
    }

    private void ToolsCloseTick(object? sender, EventArgs e)
        => CloseToolsFlyout();

    private void CloseToolsFlyout()
    {
        CancelToolsClose();
        if (_toolsPopup != null)
            _toolsPopup.IsOpen = false;
        if (_toolsParentRow != null)
        {
            _toolsParentRow.Background = WpfBrushes.Transparent;
            _toolsParentRow.BorderBrush = WpfBrushes.Transparent;
        }

        if (_toolsParentAccent != null)
            _toolsParentAccent.Background = WpfBrushes.Transparent;
    }

    private FrameworkElement BuildSubItem(
        string label, Action action, bool forTools = false, ImageSource? icon = null)
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

        FrameworkElement content = text;
        if (forTools)
        {
            var rowContent = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconBox = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            if (icon != null)
            {
                iconBox.Child = new System.Windows.Controls.Image
                {
                    Source = icon,
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
            }

            rowContent.Children.Add(iconBox);
            rowContent.Children.Add(text);
            content = rowContent;
        }

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
            if (forTools) CancelToolsClose();
            else CancelSwitchClose();
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
        CancelSwitchClose();
        CancelToolsClose();
        try
        {
            if (_switchPopup != null)
                _switchPopup.IsOpen = false;
        }
        catch { /* ignore */ }
        try
        {
            if (_toolsPopup != null)
                _toolsPopup.IsOpen = false;
        }
        catch { /* ignore */ }

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
