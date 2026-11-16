using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Ui.Theme.Maui.Icons;

/// <summary>通过图标码创建 Path，避免各端重复几何字符串。</summary>
public static class MauiIcon
{
    private static readonly PathGeometryConverter Converter = new();

    public static Geometry? Parse(string iconCode)
    {
        var data = IconCatalog.GetPath(iconCode);
        return Converter.ConvertFromInvariantString(data) as Geometry;
    }

    public static ShapePath Create(string iconCode, Color fill, double size = 16)
        => new()
        {
            Data = Parse(iconCode),
            Fill = fill,
            Aspect = Stretch.Uniform,
            WidthRequest = size,
            HeightRequest = size,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };

    public static void SetData(ShapePath path, string iconCode)
        => path.Data = Parse(iconCode);
}
