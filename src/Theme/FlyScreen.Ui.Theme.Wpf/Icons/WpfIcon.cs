using System.Windows.Media;
using System.Windows.Shapes;
using FlyScreen.Ui.Theme.Icons;

namespace FlyScreen.Ui.Theme.Wpf.Icons;

/// <summary>通过图标码创建 WPF Path。</summary>
public static class WpfIcon
{
    public static Geometry GeometryOf(string iconCode)
        => System.Windows.Media.Geometry.Parse(IconCatalog.GetPath(iconCode));

    public static Path Create(string iconCode, Brush fill, double size = 16)
        => new()
        {
            Data = GeometryOf(iconCode),
            Fill = fill,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size
        };
}
