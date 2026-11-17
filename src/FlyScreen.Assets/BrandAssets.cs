namespace FlyScreen.Assets;

/// <summary>品牌资源清单。文件在本项目内，逻辑名按目录加点。</summary>
public static class BrandAssets
{
    public const string AppIconIco = "Icons/app.ico";
    public const string AppIconPng = "Icons/appicon.png";
    public const string AppIconPng64 = "Icons/appicon-64.png";
    public const string CursorPng = "Images/cursor.png";
    /// <summary>自定义主题默认壁纸（嵌入；切换到自定义且无用户图时种入 Ui/Wallpaper）。</summary>
    public const string DefaultWallpaperPng = "Images/bg_default.png";

    public static Stream Open(string relativePath)
    {
        var name = "FlyScreen.Assets." + relativePath.Replace('/', '.').Replace('\\', '.');
        var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream(name);
        if (stream == null)
            throw new FileNotFoundException("找不到品牌资源 " + relativePath, name);
        return stream;
    }
}
