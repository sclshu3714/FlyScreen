namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>AbsoluteLayout 浮层：菜单按锚点放置，不撑开父布局。</summary>
internal sealed class VideoWallFlyoutHost
{
    private readonly AbsoluteLayout _layer;
    private View? _menu;
    private VideoWallDropDown? _owner;

    public VideoWallFlyoutHost(AbsoluteLayout layer)
    {
        _layer = layer;
        _layer.IsVisible = false;
        _layer.ZIndex = 300;
        _layer.BackgroundColor = Colors.Transparent;
        var dismiss = new TapGestureRecognizer();
        dismiss.Tapped += (_, _) => Close();
        _layer.GestureRecognizers.Add(dismiss);
    }

    public void Show(VideoWallDropDown owner, View anchor, View menu, double minWidth)
    {
        if (!ReferenceEquals(_owner, owner))
            Close();

        _owner = owner;
        _menu = menu;
        _layer.Children.Clear();

        var dim = new BoxView { Color = Colors.Transparent };
        AbsoluteLayout.SetLayoutBounds(dim, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(dim, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        var dimTap = new TapGestureRecognizer();
        dimTap.Tapped += (_, _) => Close();
        dim.GestureRecognizers.Add(dimTap);
        _layer.Children.Add(dim);

        menu.GestureRecognizers.Add(new TapGestureRecognizer());
        var w = Math.Max(minWidth, 128);
        AbsoluteLayout.SetLayoutFlags(menu, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);
        AbsoluteLayout.SetLayoutBounds(menu, new Rect(0, 0, w, AbsoluteLayout.AutoSize));
        _layer.Children.Add(menu);
        _layer.IsVisible = true;

        void Place()
        {
            if (!ReferenceEquals(_menu, menu) || !_layer.IsVisible) return;
            Position(anchor, menu, minWidth);
        }

        _layer.Dispatcher.Dispatch(() =>
        {
            Place();
            _layer.Dispatcher.Dispatch(Place);
        });
    }

    public void Close(VideoWallDropDown? onlyIfOwner = null)
    {
        if (onlyIfOwner is not null && !ReferenceEquals(_owner, onlyIfOwner))
            return;
        _layer.Children.Clear();
        _layer.IsVisible = false;
        var owner = _owner;
        _menu = null;
        _owner = null;
        owner?.NotifyFlyoutClosed();
    }

    private void Position(View anchor, View menu, double minWidth)
    {
        try
        {
            var pos = GetAnchorInLayer(anchor);
            var w = Math.Max(Math.Max(anchor.Width, minWidth), menu.Width > 1 ? menu.Width : minWidth);
            var menuH = menu.Height > 1 ? menu.Height : EstimateMenuHeight(menu);
            var layerW = _layer.Width > 1 ? _layer.Width : 800;
            var layerH = _layer.Height > 1 ? _layer.Height : 600;

            var x = pos.X;
            var y = pos.Y + Math.Max(anchor.Height, 1) + 4;

            if (x + w > layerW - 8)
                x = Math.Max(8, layerW - w - 8);
            if (x < 8) x = 8;
            if (y + menuH > layerH - 8)
                y = Math.Max(8, pos.Y - menuH - 4);

            AbsoluteLayout.SetLayoutFlags(menu, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);
            AbsoluteLayout.SetLayoutBounds(menu, new Rect(x, y, w, AbsoluteLayout.AutoSize));
        }
        catch
        {
            AbsoluteLayout.SetLayoutBounds(menu, new Rect(24, 80, Math.Max(minWidth, 128), AbsoluteLayout.AutoSize));
        }
    }

    private static double EstimateMenuHeight(View menu)
    {
        if (menu is Border { Content: VerticalStackLayout stack })
            return Math.Max(40, stack.Children.Count * 36 + 8);
        return 160;
    }

    private Point GetAnchorInLayer(View anchor)
    {
#if WINDOWS
        try
        {
            if (anchor.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe
                && _layer.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement overlay)
            {
                var a = fe.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                var o = overlay.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                return new Point(a.X - o.X, a.Y - o.Y);
            }
        }
        catch { /* fall through */ }
#endif
        var shell = _layer.Parent as VisualElement;
        var (ax, ay) = Accumulate(anchor, shell);
        var (lx, ly) = Accumulate(_layer, shell);
        return new Point(ax - lx, ay - ly);
    }

    private static (double X, double Y) Accumulate(VisualElement? from, VisualElement? stop)
    {
        double x = 0, y = 0;
        var cur = from;
        while (cur != null && !ReferenceEquals(cur, stop) && cur is not Page)
        {
            x += cur.X;
            y += cur.Y;
            if (cur is View v)
            {
                x += v.Margin.Left;
                y += v.Margin.Top;
            }

            if (cur.Parent is ScrollView sv)
            {
                x -= sv.ScrollX;
                y -= sv.ScrollY;
            }

            cur = cur.Parent as VisualElement;
        }

        return (x, y);
    }
}
