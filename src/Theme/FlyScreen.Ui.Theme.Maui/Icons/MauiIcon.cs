using FlyScreen.Ui.Theme.Icons;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Ui.Theme.Maui.Icons;

/// <summary>
/// 通过图标码创建 Path / Geometry。
/// MAUI XamlC 要求 <see cref="ShapePath.Data"/> 为 <see cref="Geometry"/>：
/// 字面量字符串可走 TypeConverter，但 <c>{x:Static}</c> 返回 string 会报 XC0009，故提供 Geometry 静态属性。
/// </summary>
public static class MauiIcon
{
    private static readonly PathGeometryConverter Converter = new();

    public static Geometry? Parse(string iconCode)
    {
        var data = IconCatalog.GetPath(iconCode);
        return Converter.ConvertFromInvariantString(data) as Geometry;
    }

    public static Geometry? ParsePathData(string pathData)
        => Converter.ConvertFromInvariantString(pathData) as Geometry;

    // 侧栏等 XAML：Data="{x:Static mauiIcons:MauiIcon.NavHome}"
    public static Geometry NavHome { get; } = Require(IconCodes.NavHome);
    public static Geometry NavNetwork { get; } = Require(IconCodes.NavNetwork);
    public static Geometry NavShare { get; } = Require(IconCodes.NavShare);
    public static Geometry NavSecurity { get; } = Require(IconCodes.NavSecurity);
    public static Geometry NavAdvanced { get; } = Require(IconCodes.NavAdvanced);
    public static Geometry NavPrograms { get; } = Require(IconCodes.NavPrograms);
    public static Geometry NavLog { get; } = Require(IconCodes.NavLog);
    public static Geometry NavAbout { get; } = Require(IconCodes.NavAbout);
    public static Geometry NavHelp { get; } = Require(IconCodes.NavHelp);

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

    private static Geometry Require(string iconCode)
        => Parse(iconCode)
           ?? throw new InvalidOperationException($"无法解析图标几何: {iconCode}");
}
