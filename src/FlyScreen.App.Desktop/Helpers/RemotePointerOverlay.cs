using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace FlyScreen.DesktopUi.Helpers;

/// <summary>
/// 被远程控制时绘制软件光标（X11/WSLg 上系统光标常不可见时的回退）。
/// 仅在 DrawMouseCursor 开启时由 Linux 宿主驱动。
/// </summary>
public sealed class RemotePointerOverlay : Window
{
    private readonly Canvas _canvas;
    private readonly Polygon _arrow;
    private bool _visible;

    public RemotePointerOverlay()
    {
        Width = 28;
        Height = 28;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        Focusable = false;

        _arrow = new Polygon
        {
            Points =
            [
                new Point(1, 1),
                new Point(1, 22),
                new Point(6, 17),
                new Point(10, 26),
                new Point(13, 25),
                new Point(9, 15),
                new Point(16, 15)
            ],
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1.2
        };

        _canvas = new Canvas { Width = 28, Height = 28 };
        _canvas.Children.Add(_arrow);
        Content = _canvas;
    }

    public void SetActive(bool active)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _visible = active;
            if (active)
            {
                if (!IsVisible) Show();
            }
            else if (IsVisible)
            {
                Hide();
            }
        });
    }

    /// <param name="nx">归一化 X 0..1</param>
    /// <param name="ny">归一化 Y 0..1</param>
    public void MoveNormalized(double nx, double ny)
    {
        if (!_visible) return;
        Dispatcher.UIThread.Post(() =>
        {
            var screen = Screens?.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            var x = (int)Math.Round(Math.Clamp(nx, 0, 1) * Math.Max(1, screen.Width - 1));
            var y = (int)Math.Round(Math.Clamp(ny, 0, 1) * Math.Max(1, screen.Height - 1));
            Position = new PixelPoint(screen.X + x, screen.Y + y);
            if (!IsVisible) Show();
        });
    }
}
