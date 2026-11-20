namespace FlyScreen.Ui.Theme.Icons;

/// <summary>
/// 统一图标码。相同图标只定义一次，各端通过 <see cref="IconCatalog.GetPath"/> 或资源键使用。
/// </summary>
public static class IconCodes
{
    // —— 通用操作 ——
    public const string Add = "icon.add";
    public const string Delete = "icon.delete";
    public const string Edit = "icon.edit";
    public const string Play = "icon.play";
    public const string Pause = "icon.pause";
    public const string Stop = "icon.stop";
    public const string Refresh = "icon.refresh";
    public const string Download = "icon.download";
    public const string Copy = "icon.copy";
    public const string Calendar = "icon.calendar";
    public const string Settings = "icon.settings";
    public const string Scan = "icon.scan";
    public const string Search = "icon.search";
    public const string Check = "icon.check";
    public const string Close = "icon.close";
    public const string Save = "icon.save";
    public const string Clock = "icon.clock";
    public const string Build = "icon.build";
    public const string Deps = "icon.deps";

    // —— 显隐 ——
    public const string EyeOpen = "icon.eye.open";
    public const string EyeOff = "icon.eye.off";

    // —— 导航 ——
    public const string NavHome = "icon.nav.home";
    public const string NavNetwork = "icon.nav.network";
    public const string NavShare = "icon.nav.share";
    public const string NavSecurity = "icon.nav.security";
    public const string NavAdvanced = "icon.nav.advanced";
    public const string NavPrograms = "icon.nav.programs";
    public const string NavLog = "icon.nav.log";
    public const string NavAbout = "icon.nav.about";
    public const string NavHelp = "icon.nav.help";
    public const string ChevronLeft = "icon.chevron.left";
    public const string ChevronRight = "icon.chevron.right";

    // —— 视图 ——
    public const string ViewCard = "icon.view.card";
    public const string ViewList = "icon.view.list";
    public const string ViewCanvas = "icon.view.canvas";

    // —— 媒体 ——
    public const string SeekBack = "icon.media.seek.back";
    public const string SeekFwd = "icon.media.seek.fwd";
    public const string SeekFwd5 = "icon.media.seek.fwd5";
    public const string Next = "icon.media.next";
    public const string Volume = "icon.media.volume";
    public const string Mute = "icon.media.mute";
    public const string Pip = "icon.media.pip";
    public const string Maximize = "icon.media.maximize";
    public const string Fullscreen = "icon.media.fullscreen";
    public const string FullscreenExit = "icon.media.fullscreen.exit";

    // —— 编辑器专用形态（与通用 Pause/Stop 不同） ——
    public const string PlayOutline = "icon.play.outline";
    public const string PauseBars = "icon.pause.bars";
    public const string StopSmall = "icon.stop.small";
}
