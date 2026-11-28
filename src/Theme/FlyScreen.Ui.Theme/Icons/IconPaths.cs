namespace FlyScreen.Ui.Theme.Icons;

/// <summary>
/// 图标码对应的 Path Data 字符串，供 XAML <c>{x:Static}</c> 绑定。
/// Avalonia 的 Path.Data 接受 string；MAUI 的 Path.Data 需要 Geometry，请用
/// <c>FlyScreen.Ui.Theme.Maui.Icons.MauiIcon</c> 的 Geometry 静态属性。
/// </summary>
public static class IconPaths
{
    public static string Add => IconCatalog.GetPath(IconCodes.Add);
    public static string Delete => IconCatalog.GetPath(IconCodes.Delete);
    public static string Edit => IconCatalog.GetPath(IconCodes.Edit);
    public static string Play => IconCatalog.GetPath(IconCodes.Play);
    public static string Pause => IconCatalog.GetPath(IconCodes.Pause);
    public static string Stop => IconCatalog.GetPath(IconCodes.Stop);
    public static string Refresh => IconCatalog.GetPath(IconCodes.Refresh);
    public static string Download => IconCatalog.GetPath(IconCodes.Download);
    public static string Copy => IconCatalog.GetPath(IconCodes.Copy);
    public static string Calendar => IconCatalog.GetPath(IconCodes.Calendar);
    public static string Settings => IconCatalog.GetPath(IconCodes.Settings);
    public static string Scan => IconCatalog.GetPath(IconCodes.Scan);
    public static string Search => IconCatalog.GetPath(IconCodes.Search);
    public static string Check => IconCatalog.GetPath(IconCodes.Check);
    public static string Close => IconCatalog.GetPath(IconCodes.Close);
    public static string Save => IconCatalog.GetPath(IconCodes.Save);
    public static string Clock => IconCatalog.GetPath(IconCodes.Clock);
    public static string Build => IconCatalog.GetPath(IconCodes.Build);
    public static string Deps => IconCatalog.GetPath(IconCodes.Deps);
    public static string EyeOpen => IconCatalog.GetPath(IconCodes.EyeOpen);
    public static string EyeOff => IconCatalog.GetPath(IconCodes.EyeOff);

    public static string NavHome => IconCatalog.GetPath(IconCodes.NavHome);
    public static string NavNetwork => IconCatalog.GetPath(IconCodes.NavNetwork);
    public static string NavShare => IconCatalog.GetPath(IconCodes.NavShare);
    public static string NavSecurity => IconCatalog.GetPath(IconCodes.NavSecurity);
    public static string NavAdvanced => IconCatalog.GetPath(IconCodes.NavAdvanced);
    public static string NavPrograms => IconCatalog.GetPath(IconCodes.NavPrograms);
    public static string NavLog => IconCatalog.GetPath(IconCodes.NavLog);
    public static string NavAbout => IconCatalog.GetPath(IconCodes.NavAbout);
    public static string NavHelp => IconCatalog.GetPath(IconCodes.NavHelp);
    public static string ChevronLeft => IconCatalog.GetPath(IconCodes.ChevronLeft);
    public static string ChevronRight => IconCatalog.GetPath(IconCodes.ChevronRight);
    public static string ChevronUp => IconCatalog.GetPath(IconCodes.ChevronUp);
    public static string WindowMinimize => IconCatalog.GetPath(IconCodes.WindowMinimize);
    public static string WindowMaximize => IconCatalog.GetPath(IconCodes.WindowMaximize);
    public static string WindowRestore => IconCatalog.GetPath(IconCodes.WindowRestore);

    public static string ViewCard => IconCatalog.GetPath(IconCodes.ViewCard);
    public static string ViewList => IconCatalog.GetPath(IconCodes.ViewList);
    public static string ViewCanvas => IconCatalog.GetPath(IconCodes.ViewCanvas);

    public static string SeekBack => IconCatalog.GetPath(IconCodes.SeekBack);
    public static string SeekFwd => IconCatalog.GetPath(IconCodes.SeekFwd);
    public static string SeekFwd5 => IconCatalog.GetPath(IconCodes.SeekFwd5);
    public static string Next => IconCatalog.GetPath(IconCodes.Next);
    public static string Volume => IconCatalog.GetPath(IconCodes.Volume);
    public static string Mute => IconCatalog.GetPath(IconCodes.Mute);
    public static string Pip => IconCatalog.GetPath(IconCodes.Pip);
    public static string Maximize => IconCatalog.GetPath(IconCodes.Maximize);
    public static string Fullscreen => IconCatalog.GetPath(IconCodes.Fullscreen);
    public static string FullscreenExit => IconCatalog.GetPath(IconCodes.FullscreenExit);

    /// <summary>程序编辑器专用：空心播放 / 细双竖暂停 / 小方块停止。</summary>
    public static string PlayOutline => IconCatalog.GetPath(IconCodes.PlayOutline);
    public static string PauseBars => IconCatalog.GetPath(IconCodes.PauseBars);
    public static string StopSmall => IconCatalog.GetPath(IconCodes.StopSmall);

    public static string FileFolder => IconCatalog.GetPath(IconCodes.FileFolder);
    public static string FileShortcut => IconCatalog.GetPath(IconCodes.FileShortcut);
    public static string FileAccess => IconCatalog.GetPath(IconCodes.FileAccess);
    public static string FileImage => IconCatalog.GetPath(IconCodes.FileImage);
    public static string FileWord => IconCatalog.GetPath(IconCodes.FileWord);
    public static string FilePpt => IconCatalog.GetPath(IconCodes.FilePpt);
    public static string FileText => IconCatalog.GetPath(IconCodes.FileText);
    public static string FileRtf => IconCatalog.GetPath(IconCodes.FileRtf);
    public static string FileExcel => IconCatalog.GetPath(IconCodes.FileExcel);
    public static string FileZip => IconCatalog.GetPath(IconCodes.FileZip);
    public static string FileGeneric => IconCatalog.GetPath(IconCodes.FileGeneric);
}
