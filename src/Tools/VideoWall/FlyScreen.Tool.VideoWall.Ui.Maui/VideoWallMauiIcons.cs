using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>视频墙 Maui 图标：统一图标码取几何，保留工具侧按钮工厂。</summary>
public static class VideoWallMauiIcons
{
    private static readonly PathGeometryConverter GeoConverter = new();

    public static class Geo
    {
        public static string Add => IconCatalog.GetPath(IconCodes.Add);
        public static string Scan => IconCatalog.GetPath(IconCodes.Scan);
        public static string Search => IconCatalog.GetPath(IconCodes.Search);
        public static string Delete => IconCatalog.GetPath(IconCodes.Delete);
        public static string Play => IconCatalog.GetPath(IconCodes.Play);
        public static string Edit => IconCatalog.GetPath(IconCodes.Edit);
        public static string Back => IconCatalog.GetPath(IconCodes.ChevronLeft);
        public static string ViewCard => IconCatalog.GetPath(IconCodes.ViewCard);
        public static string ViewList => IconCatalog.GetPath(IconCodes.ViewList);
        public static string ViewCanvas => IconCatalog.GetPath(IconCodes.ViewCanvas);
        public static string Pause => IconCatalog.GetPath(IconCodes.Pause);
        public static string Stop => IconCatalog.GetPath(IconCodes.Stop);
        public static string SeekBack => IconCatalog.GetPath(IconCodes.SeekBack);
        public static string SeekFwd => IconCatalog.GetPath(IconCodes.SeekFwd);
        public static string Next => IconCatalog.GetPath(IconCodes.Next);
        public static string SeekFwd5 => IconCatalog.GetPath(IconCodes.SeekFwd5);
        public static string Settings => IconCatalog.GetPath(IconCodes.Settings);
        public static string Pip => IconCatalog.GetPath(IconCodes.Pip);
        public static string Maximize => IconCatalog.GetPath(IconCodes.Maximize);
        public static string FullscreenExit => IconCatalog.GetPath(IconCodes.FullscreenExit);
        public static string Volume => IconCatalog.GetPath(IconCodes.Volume);
        public static string Mute => IconCatalog.GetPath(IconCodes.Mute);
        public static string Fullscreen => IconCatalog.GetPath(IconCodes.Fullscreen);
        /// <summary>横向三点「更多」（操作栏溢出）。</summary>
        public static string More =>
            "M16,12A2,2 0 0,1 18,10A2,2 0 0,1 20,12A2,2 0 0,1 18,14A2,2 0 0,1 16,12M10,12A2,2 0 0,1 12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12M4,12A2,2 0 0,1 6,10A2,2 0 0,1 8,12A2,2 0 0,1 6,14A2,2 0 0,1 4,12Z";
    }

    public enum ToolbarKind { Normal, Primary, Danger }
    public enum SegmentCorner { Left, Middle, Right }

    public readonly struct IconButtonPair(Border host, Path icon)
    {
        public Border Host { get; } = host;
        public Path Icon { get; } = icon;
    }

    public static Path CreatePath(string dataOrIconCode, Color fill, double size = 16)
    {
        return new Path
        {
            Data = ParseGeometry(ResolvePathData(dataOrIconCode)),
            Fill = fill,
            Aspect = Stretch.Uniform,
            WidthRequest = size,
            HeightRequest = size,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
    }

    public static void SetPathData(Path path, string dataOrIconCode)
        => path.Data = ParseGeometry(ResolvePathData(dataOrIconCode));

    public static IconButtonPair CreateIconButton(
        string geoData,
        Color iconFill,
        string hintText,
        ToolbarKind kind,
        Color text,
        Color card,
        Color line,
        Color blue,
        Color warn,
        Thickness? margin = null)
    {
        var icon = CreatePath(geoData, iconFill, 14);
        var (bg, border, _) = kind switch
        {
            ToolbarKind.Primary => (blue, blue, Colors.White),
            ToolbarKind.Danger => (card, Color.FromArgb("#FECACA"), warn),
            _ => (card, line, text)
        };
        if (kind == ToolbarKind.Primary)
            icon.Fill = Colors.White;

        var host = CreateTapHost(bg, border, icon, 32, margin ?? new Thickness(0, 0, 6, 0), 6);
        ApplyHint(host, hintText);
        return new IconButtonPair(host, icon);
    }

    /// <summary>播放器 chrome 用：透明底、无描边、白色 Path 图标；悬停浅亮。</summary>
    public static IconButtonPair CreateChromeIconButton(
        string geoData,
        string hintText,
        Thickness? margin = null,
        double iconSize = 16)
    {
        var icon = CreatePath(geoData, Colors.White, iconSize);
        var host = CreateTapHost(
            Colors.Transparent,
            Colors.Transparent,
            icon,
            34,
            margin ?? new Thickness(0, 0, 4, 0),
            6);
        host.StrokeThickness = 0;
        ApplyHint(host, hintText);
        WireChromeHover(host);
        return new IconButtonPair(host, icon);
    }

    /// <summary>播放器 chrome 悬停：半透明白底，对齐 Win ChromeIconBtn。</summary>
    public static void WireChromeHover(View host, Color? hover = null)
    {
        var idle = host.BackgroundColor ?? Colors.Transparent;
        var over = hover ?? Color.FromArgb("#33FFFFFF");
        var ptr = new PointerGestureRecognizer();
        ptr.PointerEntered += (_, _) => host.BackgroundColor = over;
        ptr.PointerExited += (_, _) => host.BackgroundColor = idle;
        host.GestureRecognizers.Add(ptr);
    }

    public static IconButtonPair CreateRowIconButton(
        string geoData,
        Color iconFill,
        string hintText,
        Color card,
        Color line)
    {
        var icon = CreatePath(geoData, iconFill, 12);
        var host = CreateTapHost(card, line, icon, 28, new Thickness(0, 0, 4, 0), 6);
        ApplyHint(host, hintText);
        return new IconButtonPair(host, icon);
    }

    public static IconButtonPair CreateSegmentIconButton(
        string geoData,
        string hintText,
        SegmentCorner corner,
        Color text,
        Color card,
        Color line)
    {
        var icon = CreatePath(geoData, text, 14);
        var radius = corner switch
        {
            SegmentCorner.Left => new CornerRadius(6, 0, 0, 6),
            SegmentCorner.Right => new CornerRadius(0, 6, 6, 0),
            _ => new CornerRadius(0)
        };
        var host = CreateTapHost(card, line, icon, 32, 0, radius);
        ApplyHint(host, hintText);
        return new IconButtonPair(host, icon);
    }

    public static void SetSegmentActive(Border host, Path icon, bool active, Color blue, Color muted, Color card, Color line)
    {
        host.BackgroundColor = active ? Color.FromArgb("#DBEAFE") : card;
        host.Stroke = active ? blue : line;
        icon.Fill = active ? blue : muted;
    }

    public static void WireClicked(Border host, EventHandler handler)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => handler(host, EventArgs.Empty);
        host.GestureRecognizers.Add(tap);
    }

    public static void WireClicked(Border host, Func<Task> handler)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await handler();
        host.GestureRecognizers.Add(tap);
    }

    private static string ResolvePathData(string dataOrIconCode)
        => IconCatalog.TryGetPath(dataOrIconCode, out var path) ? path : dataOrIconCode;

    private static Border CreateTapHost(Color bg, Color stroke, View icon, double size, Thickness margin, CornerRadius radius)
    {
        return new Border
        {
            BackgroundColor = bg,
            Stroke = stroke,
            StrokeThickness = 1,
            Padding = 0,
            WidthRequest = size,
            HeightRequest = size,
            MinimumWidthRequest = size,
            MinimumHeightRequest = size,
            Margin = margin,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle { CornerRadius = radius },
            Content = icon
        };
    }

    private static Geometry? ParseGeometry(string data)
        => GeoConverter.ConvertFromInvariantString(data) as Geometry;

    private static void ApplyHint(VisualElement el, string hintText)
    {
        SemanticProperties.SetDescription(el, hintText);
        SemanticProperties.SetHint(el, hintText);
        AutomationProperties.SetHelpText(el, hintText);
        AutomationProperties.SetName(el, hintText);
    }
}
