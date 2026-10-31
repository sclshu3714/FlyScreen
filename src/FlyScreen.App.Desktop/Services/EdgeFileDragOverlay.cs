using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FlyScreen.DesktopUi.Shell;

namespace FlyScreen.DesktopUi.Services;

/// <summary>
/// 贴在屏幕四边的窄条。文件拖上来后交给宿主，按布局穿到旁边的机器。
/// </summary>
public sealed class EdgeFileDragOverlay : IDisposable
{
    private readonly IDesktopShellHost _host;
    private readonly List<Window> _strips = [];

    public EdgeFileDragOverlay(Window owner, IDesktopShellHost host)
    {
        _host = host;
        var screen = owner.Screens?.Primary ?? owner.Screens?.All.FirstOrDefault();
        if (screen == null) return;
        var bounds = screen.Bounds;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var width = bounds.Width / scale;
        var height = bounds.Height / scale;
        const double strip = 14;
        Add(owner, -1, bounds.X, bounds.Y, strip, height);
        Add(owner, 1, bounds.X + bounds.Width - (int)(strip * scale), bounds.Y, strip, height);
        Add(owner, -2, bounds.X, bounds.Y, width, strip);
        Add(owner, 2, bounds.X, bounds.Y + bounds.Height - (int)(strip * scale), width, strip);
        _host.EdgeFileDragActiveChanged += OnActiveChanged;
    }

    public void Dispose()
    {
        _host.EdgeFileDragActiveChanged -= OnActiveChanged;
        foreach (var strip in _strips)
        {
            try { strip.Close(); }
            catch { /* ignore */ }
        }

        _strips.Clear();
    }

    private void Add(Window owner, int side, int x, int y, double width, double height)
    {
        var strip = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            Topmost = true,
            Focusable = false,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Width = Math.Max(8, width),
            Height = Math.Max(8, height),
            Position = new PixelPoint(x, y),
            Tag = side
        };
        DragDrop.SetAllowDrop(strip, true);
        strip.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        strip.AddHandler(DragDrop.DropEvent, OnDrop);
        strip.Show();
        _strips.Add(strip);
        _ = owner;
    }

    private void OnActiveChanged(bool active)
    {
        foreach (var strip in _strips)
            strip.IsVisible = !active;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var paths = ReadPaths(e);
        if (paths.Count == 0) return;
        e.DragEffects = DragDropEffects.Copy;
        if (!_host.CanFollowEdgeFileDrag) return;
        if (sender is not Control strip || strip.Tag is not int side) return;
        _host.OfferEdgeFileDrag(paths, side);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = ReadPaths(e);
        if (paths.Count == 0) return;
        e.DragEffects = DragDropEffects.Copy;
        if (_host.CanFollowEdgeFileDrag) return;
        if (sender is not Control strip || strip.Tag is not int side) return;
        _host.OfferEdgeFileDrag(paths, side);
    }

    private static List<string> ReadPaths(DragEventArgs e)
    {
        var list = new List<string>();
        var items = e.Data.GetFiles();
        if (items == null) return list;
        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is not string path || path.Length == 0) continue;
            if (File.Exists(path) || Directory.Exists(path))
                list.Add(path);
        }

        return list;
    }
}
