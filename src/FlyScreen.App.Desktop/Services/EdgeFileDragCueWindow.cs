using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Services.Clipboard;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// Mac / Linux 上跟光标走的拖拽提示。放下位置由宿主说明（通常是桌面）。
/// </summary>
public sealed class EdgeFileDragCueWindow : IDisposable
{
    private readonly IDesktopShellHost _host;
    private readonly Window _window;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer;
    private bool _active;

    public EdgeFileDragCueWindow(IDesktopShellHost host)
    {
        _host = host;
        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(32, 36, 40)),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap
        };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 190, 200)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Child = _text
        };
        _window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = border,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _timer.Tick += (_, _) => Move();
    }

    public void OnCue(EdgeDragCue? cue)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (cue == null || cue.Count <= 0)
            {
                _active = false;
                _timer.Stop();
                if (_window.IsVisible)
                    _window.Hide();
                return;
            }

            _text.Text = BuildText(cue, _host.EdgeDragDropLabel);
            _active = true;
            if (!_window.IsVisible)
                _window.Show();
            Move();
            _timer.Start();
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _active = false;
        try
        {
            if (_window.IsVisible)
                _window.Close();
        }
        catch
        {
            // ignore
        }
    }

    private void Move()
    {
        if (!_active) return;
        var screen = _window.Screens?.Primary ?? _window.Screens?.All.FirstOrDefault();
        var bounds = screen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        var has = _host.TryGetEdgeDragPointer(out var px, out var py);
        var x = has ? px + 18 : bounds.X + bounds.Width / 2;
        var y = has ? py + 22 : bounds.Y + bounds.Height / 2;
        var w = Math.Max(40, (int)_window.Bounds.Width);
        var h = Math.Max(24, (int)_window.Bounds.Height);
        if (x + w > bounds.X + bounds.Width)
            x = (has ? px : x) - w - 12;
        if (y + h > bounds.Y + bounds.Height)
            y = (has ? py : y) - h - 12;
        x = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.X + bounds.Width - w));
        y = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Y + bounds.Height - h));
        _window.Position = new PixelPoint(x, y);
    }

    private static string BuildText(EdgeDragCue cue, string place)
    {
        var lines = new List<string> { cue.Count > 1 ? $"复制 {cue.Count} 项" : "复制 1 项" };
        var names = cue.Names.Where(n => !string.IsNullOrWhiteSpace(n)).Take(4).ToArray();
        lines.AddRange(names);
        if (cue.Count > names.Length && names.Length > 0)
            lines.Add($"以及其他 {cue.Count - names.Length} 项");
        var where = string.IsNullOrWhiteSpace(place) ? "桌面" : place;
        lines.Add($"松开复制到{where}");
        return string.Join('\n', lines);
    }
}
