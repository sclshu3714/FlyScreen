using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// WSL/X11 系统标题栏常无法正确显示中文（乱码方框），且与主窗自绘风格不一致。
/// 对子窗关闭系统装饰，改用客户端标题栏（与 MainWindow 同思路）。
/// </summary>
internal sealed class LinuxClientChrome
{
    private readonly Window _window;
    private readonly Border _titleBar;
    private readonly TextBlock _titleText;
    private readonly bool _allowMaximize;
    private bool _moving;
    private int _grabOffsetX;
    private int _grabOffsetY;

    private LinuxClientChrome(Window window, Border titleBar, TextBlock titleText, bool allowMaximize)
    {
        _window = window;
        _titleBar = titleBar;
        _titleText = titleText;
        _allowMaximize = allowMaximize;
    }

    public static LinuxClientChrome? Attach(
        Window window,
        Border titleBar,
        TextBlock titleText,
        string displayTitle,
        Button? minBtn = null,
        Button? maxBtn = null,
        Button? closeBtn = null,
        bool allowMaximize = true)
    {
        titleText.Text = displayTitle;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            titleBar.IsVisible = false;
            window.Title = displayTitle;
            return null;
        }

        // 任务栏/Alt-Tab 用 ASCII，避免系统装饰短暂露出时乱码
        window.Title = "FlyScreen";
        var chrome = new LinuxClientChrome(window, titleBar, titleText, allowMaximize);
        window.SystemDecorations = SystemDecorations.None;
        titleBar.IsVisible = true;
        window.Background = titleBar.Background ?? Brushes.White;

        if (window.Content is Border frame)
        {
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(8);
        }

        titleBar.PointerPressed += chrome.OnPointerPressed;
        titleBar.PointerMoved += chrome.OnPointerMoved;
        titleBar.PointerReleased += chrome.OnPointerReleased;
        titleBar.PointerCaptureLost += chrome.OnPointerCaptureLost;

        if (minBtn != null)
            minBtn.Click += (_, _) => window.WindowState = WindowState.Minimized;
        if (maxBtn != null)
        {
            maxBtn.IsVisible = allowMaximize;
            maxBtn.Click += (_, _) =>
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
        }

        if (closeBtn != null)
            closeBtn.Click += (_, _) => window.Close();

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            if (window.Content is Border f)
                f.CornerRadius = window.WindowState == WindowState.Maximized
                    ? default
                    : new CornerRadius(8);
        };

        return chrome;
    }

    public void SetTitle(string displayTitle) => _titleText.Text = displayTitle;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed)
            return;
        if (e.Source is Control src && IsButton(src))
            return;

        if (_allowMaximize && e.ClickCount >= 2)
        {
            EndMove(e.Pointer);
            _window.WindowState = _window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (wayland)
        {
            try
            {
                _window.BeginMoveDrag(e);
                e.Handled = true;
                return;
            }
            catch
            {
                /* fall through */
            }
        }

        StartManualMove(e);
    }

    private void StartManualMove(PointerPressedEventArgs e)
    {
        if (!LinuxScreenPointer.TryGetPosition(out var px, out var py))
        {
            var screen = _window.PointToScreen(e.GetPosition(_window));
            px = screen.X;
            py = screen.Y;
        }

        _grabOffsetX = px - _window.Position.X;
        _grabOffsetY = py - _window.Position.Y;
        _moving = true;
        e.Pointer.Capture(_titleBar);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_moving) return;
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed)
        {
            EndMove(e.Pointer);
            return;
        }

        if (!LinuxScreenPointer.TryGetPosition(out var px, out var py))
        {
            var screen = _window.PointToScreen(e.GetPosition(_window));
            px = screen.X;
            py = screen.Y;
        }

        var next = new PixelPoint(px - _grabOffsetX, py - _grabOffsetY);
        if (next != _window.Position)
            _window.Position = next;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndMove(e.Pointer);

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndMove(null);

    private void EndMove(IPointer? pointer)
    {
        if (!_moving && pointer == null) return;
        _moving = false;
        try { pointer?.Capture(null); } catch { /* ignore */ }
    }

    private static bool IsButton(Control? c)
    {
        while (c != null)
        {
            if (c is Button) return true;
            c = c.GetVisualParent() as Control;
        }

        return false;
    }
}
