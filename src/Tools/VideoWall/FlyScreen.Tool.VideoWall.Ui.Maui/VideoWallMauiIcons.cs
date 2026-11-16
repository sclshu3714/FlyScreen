using Microsoft.Maui.Controls.Shapes;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>视频墙 Maui 图标几何与按钮工厂（对齐 Win WPF Path 数据）。</summary>
public static class VideoWallMauiIcons
{
    private static readonly PathGeometryConverter GeoConverter = new();

    public static class Geo
    {
        public const string Add = "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z";
        public const string Scan = "M12,2A10,10 0 0,1 22,12A10,10 0 0,1 12,22A10,10 0 0,1 2,12A10,10 0 0,1 12,2M12,6A6,6 0 0,0 6,12A6,6 0 0,0 12,18A6,6 0 0,0 18,12A6,6 0 0,0 12,6M12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12A2,2 0 0,1 12,10Z";
        public const string Delete = "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z";
        public const string Play = "M8,5.14V19.14L19,12.14L8,5.14Z";
        public const string Edit = "M3,17.25V21H6.75L17.81,9.94L14.06,6.19L3,17.25M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.13,5.12L18.88,8.87L20.71,7.04Z";
        public const string Back = "M15.41,16.58L10.83,12L15.41,7.41L14,6L8,12L14,18L15.41,16.58Z";
        public const string ViewCard = "M3,3H10V10H3V3M14,3H21V10H14V3M3,14H10V21H3V14M14,14H21V21H14V14Z";
        public const string ViewList = "M3,5H21V7H3V5M3,11H21V13H3V11M3,17H21V19H3V17Z";
        public const string ViewCanvas = "M3,3H21V15H3V3M5,5V13H19V5H5M8,17H16V19H8V17M10,19H14V21H10V19Z";
        public const string Pause = "M14,19H18V5H14M6,19H10V5H6V19Z";
        public const string Stop = "M18,18H6V6H18V18Z";
        public const string SeekBack = "M11.5,12L20,18V6M11,18V6L2.5,12L11,18Z";
        public const string SeekFwd = "M13,6V18L21.5,12M4,18L12.5,12L4,6V18Z";
        public const string Next = "M6,18L14.5,12L6,6V18M16,6V18H18V6H16Z";
        /// <summary>快进 5 秒：顺时针回环箭头（数字 5 由控件叠加）。</summary>
        public const string SeekFwd5 = "M12,5V1L17,6L12,11V7C8.69,7 6,9.69 6,13C6,16.31 8.69,19 12,19C15.31,19 18,16.31 18,13H20C20,17.42 16.42,21 12,21C7.58,21 4,17.42 4,13C4,8.58 7.58,5 12,5Z";
        public const string Settings = "M12,2L20.66,7V17L12,22L3.34,17V7L12,2M12,8.5A3.5,3.5 0 1 0 12,15.5A3.5,3.5 0 1 0 12,8.5Z";
        public const string Pip = "M19,11H11V17H19V11M23,19V5C23,3.88 22.1,3 21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19M21,19H3V4.97H21V19Z";
        public const string Maximize = "M4,4H20V20H4V4M6,8V18H18V8H6Z";
        public const string FullscreenExit = "M14,14H19V16H16V19H14V14M5,14H10V19H8V16H5V14M8,5H10V10H5V8H8V5M16,8V5H14V10H19V8H16Z";
        public const string Volume = "M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.84 14,18.7V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.76 16.5,12M3,9V15H7L12,20V4L7,9H3Z";
        public const string Mute = "M12,4L9.91,6.09L12,8.18M4.27,3L3,4.27L7.73,9H3V15H7L12,20V13.27L16.25,17.53C15.58,18.04 14.83,18.46 14,18.7V20.77C15.38,20.45 16.63,19.82 17.68,18.96L19.73,21L21,19.73L12,10.73M19,12C19,12.94 18.8,13.82 18.46,14.64L19.97,16.15C20.62,14.91 21,13.5 21,12C21,7.72 18,4.14 14,3.23V5.29C16.89,6.15 19,8.83 19,12M16.5,12C16.5,10.23 15.5,8.71 14,7.97V10.18L16.45,12.63C16.5,12.43 16.5,12.21 16.5,12Z";
        public const string Fullscreen = "M5,5H10V7H7V10H5V5M14,5H19V10H17V7H14V5M17,14H19V19H14V17H17V14M10,17V19H5V14H7V17H10Z";
    }

    public enum ToolbarKind { Normal, Primary, Danger }
    public enum SegmentCorner { Left, Middle, Right }

    public readonly struct IconButtonPair(Border host, Path icon)
    {
        public Border Host { get; } = host;
        public Path Icon { get; } = icon;
    }

    public static Path CreatePath(string data, Color fill, double size = 16)
    {
        var path = new Path
        {
            Data = ParseGeometry(data),
            Fill = fill,
            Aspect = Stretch.Uniform,
            WidthRequest = size,
            HeightRequest = size,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        return path;
    }

    public static void SetPathData(Path path, string data) => path.Data = ParseGeometry(data);

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
        var iconSize = 14.0;
        var icon = CreatePath(geoData, iconFill, iconSize);
        var (bg, border, fg) = kind switch
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