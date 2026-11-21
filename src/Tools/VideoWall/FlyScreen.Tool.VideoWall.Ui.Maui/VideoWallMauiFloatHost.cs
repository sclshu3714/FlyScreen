namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// 在视频墙面板 AbsoluteLayout 上挂载可拖拽/缩放的浮窗壳（手机与 Maui-Win 共用，不改宿主 AppWindow）。
/// </summary>
internal sealed class VideoWallMauiFloatHost
{
    private const double DefaultWidth = 320;
    private const double DefaultHeight = 200;
    private const double MinWidth = 200;
    private const double MinHeight = 140;
    private const double EdgeMargin = 12;

    private readonly AbsoluteLayout _layer;
    private readonly Border _shell;
    private readonly Grid _contentSlot;
    private readonly BoxView _resizeHandle;
    private View? _content;
    private View? _originParent;
    private int _originIndex;
    private Rect _pipBounds;
    private bool _dragging;
    private bool _resizing;
    private Point _panStart;
    private Rect _boundsAtPanStart;

    public enum Mode { Pip, Maximized, Fullscreen }

    public Mode CurrentMode { get; private set; } = Mode.Pip;
    public bool IsOpen => _shell.Parent is not null;

    public VideoWallMauiFloatHost(AbsoluteLayout layer)
    {
        _layer = layer;
        _contentSlot = new Grid { BackgroundColor = Colors.Black };
        _resizeHandle = new BoxView
        {
            Color = Color.FromArgb("#66FFFFFF"),
            WidthRequest = 18,
            HeightRequest = 18,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 4, 4)
        };

        _shell = new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#44FFFFFF"),
            BackgroundColor = Colors.Black,
            Padding = 0,
            ZIndex = 500,
            Content = new Grid
            {
                Children =
                {
                    _contentSlot,
                    _resizeHandle
                }
            }
        };

        var dragPan = new PanGestureRecognizer();
        dragPan.PanUpdated += OnShellPan;
        _shell.GestureRecognizers.Add(dragPan);

        var resizePan = new PanGestureRecognizer();
        resizePan.PanUpdated += OnResizePan;
        _resizeHandle.GestureRecognizers.Add(resizePan);
    }

    public void Open(View content, Mode mode)
    {
        if (content.Parent is View parent)
        {
            _originParent = parent;
            _originIndex = parent is Layout layout ? layout.Children.IndexOf(content) : -1;
            if (parent is Layout pl)
                pl.Children.Remove(content);
            else if (parent is Border b && ReferenceEquals(b.Content, content))
                b.Content = null;
            else if (parent is ContentView cv && ReferenceEquals(cv.Content, content))
                cv.Content = null;
        }

        _content = content;
        _contentSlot.Children.Clear();
        _contentSlot.Children.Add(content);

        var firstOpen = _shell.Parent is null;
        if (firstOpen)
            _layer.Children.Add(_shell);

        ApplyMode(mode, resetPipBounds: firstOpen || _pipBounds.Width <= 0);
    }

    public void ApplyMode(Mode mode, bool resetPipBounds = false)
    {
        CurrentMode = mode;
        _resizeHandle.IsVisible = mode == Mode.Pip;

        if (mode == Mode.Maximized || mode == Mode.Fullscreen)
        {
            AbsoluteLayout.SetLayoutFlags(_shell, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
            AbsoluteLayout.SetLayoutBounds(_shell, new Rect(0, 0, 1, 1));
            _shell.StrokeThickness = mode == Mode.Fullscreen ? 0 : 1;
            return;
        }

        if (resetPipBounds || _pipBounds.Width <= 0)
            _pipBounds = ComputeDefaultPipBounds();

        AbsoluteLayout.SetLayoutFlags(_shell, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);
        AbsoluteLayout.SetLayoutBounds(_shell, _pipBounds);
        _shell.StrokeThickness = 1;
    }

    public void Close()
    {
        if (_content is not null)
        {
            _contentSlot.Children.Remove(_content);
            RestoreContent(_content);
            _content = null;
        }

        if (_shell.Parent is Layout lay)
            lay.Children.Remove(_shell);

        _originParent = null;
        CurrentMode = Mode.Pip;
    }

    private void RestoreContent(View content)
    {
        if (_originParent is Layout layout)
        {
            var idx = Math.Clamp(_originIndex < 0 ? layout.Children.Count : _originIndex, 0, layout.Children.Count);
            layout.Children.Insert(idx, content);
            return;
        }

        if (_originParent is Border b)
            b.Content = content;
        else if (_originParent is ContentView cv)
            cv.Content = content;
    }

    private Rect ComputeDefaultPipBounds()
    {
        var hostW = Math.Max(_layer.Width, 360);
        var hostH = Math.Max(_layer.Height, 240);
        var w = Math.Min(DefaultWidth, Math.Max(MinWidth, hostW * 0.42));
        var h = Math.Min(DefaultHeight, Math.Max(MinHeight, hostH * 0.36));
        // 「左面右边居中」：落在宿主左半区域的右侧、垂直居中（手机单屏即屏幕右侧居中）
        var leftHalfRight = hostW * 0.5;
        var x = Math.Max(EdgeMargin, leftHalfRight - w - EdgeMargin);
        // 若宿主较宽（桌面），改到最左侧工作区的右侧：用整宽右侧
        if (hostW > 700)
            x = Math.Max(EdgeMargin, hostW - w - EdgeMargin);
        var y = Math.Max(EdgeMargin, (hostH - h) / 2);
        return new Rect(x, y, w, h);
    }

    private void OnShellPan(object? sender, PanUpdatedEventArgs e)
    {
        if (CurrentMode != Mode.Pip || _resizing) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragging = true;
                _panStart = new Point(e.TotalX, e.TotalY);
                _boundsAtPanStart = AbsoluteLayout.GetLayoutBounds(_shell);
                break;
            case GestureStatus.Running when _dragging:
                var b = _boundsAtPanStart;
                var nx = Clamp(b.X + e.TotalX, 0, Math.Max(0, _layer.Width - b.Width));
                var ny = Clamp(b.Y + e.TotalY, 0, Math.Max(0, _layer.Height - b.Height));
                _pipBounds = new Rect(nx, ny, b.Width, b.Height);
                AbsoluteLayout.SetLayoutBounds(_shell, _pipBounds);
                break;
            default:
                _dragging = false;
                break;
        }
    }

    private void OnResizePan(object? sender, PanUpdatedEventArgs e)
    {
        if (CurrentMode != Mode.Pip || _dragging) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _resizing = true;
                _boundsAtPanStart = AbsoluteLayout.GetLayoutBounds(_shell);
                break;
            case GestureStatus.Running when _resizing:
                var b = _boundsAtPanStart;
                var nw = Clamp(b.Width + e.TotalX, MinWidth, Math.Max(MinWidth, _layer.Width - b.X));
                var nh = Clamp(b.Height + e.TotalY, MinHeight, Math.Max(MinHeight, _layer.Height - b.Y));
                _pipBounds = new Rect(b.X, b.Y, nw, nh);
                AbsoluteLayout.SetLayoutBounds(_shell, _pipBounds);
                break;
            default:
                _resizing = false;
                break;
        }
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));
}
