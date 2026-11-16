namespace FlyScreen.Ui.Theme.Icons;

/// <summary>图标码 → Path Data（Material Design 风格几何）。</summary>
public static class IconCatalog
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        [IconCodes.Add] = "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z",
        [IconCodes.Delete] = "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z",
        [IconCodes.Edit] = "M3,17.25V21H6.75L17.81,9.94L14.06,6.19L3,17.25M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.13,5.12L18.88,8.87L20.71,7.04Z",
        [IconCodes.Play] = "M8,5.14V19.14L19,12.14L8,5.14Z",
        [IconCodes.Pause] = "M14,19H18V5H14M6,19H10V5H6V19Z",
        [IconCodes.Stop] = "M6,6H18V18H6V6Z",
        [IconCodes.Refresh] = "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z",
        [IconCodes.Download] = "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z",
        [IconCodes.Copy] = "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z",
        [IconCodes.Calendar] = "M19,19H5V8H19M16,1V3H8V1H6V3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3H18V1M17,12H12V17H17V12Z",
        [IconCodes.Settings] = "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z",
        [IconCodes.Scan] = "M12,2A10,10 0 0,1 22,12A10,10 0 0,1 12,22A10,10 0 0,1 2,12A10,10 0 0,1 12,2M12,6A6,6 0 0,0 6,12A6,6 0 0,0 12,18A6,6 0 0,0 18,12A6,6 0 0,0 12,6M12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12A2,2 0 0,1 12,10Z",
        [IconCodes.Search] = "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z",
        [IconCodes.Check] = "M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z",
        [IconCodes.Close] = "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z",
        [IconCodes.Save] = "M15,9H5V5H15M12,19A3,3 0 0,1 9,16A3,3 0 0,1 12,13A3,3 0 0,1 15,16A3,3 0 0,1 12,19M17,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V7L17,3Z",
        [IconCodes.Clock] = "M12,20A8,8 0 0,0 20,12A8,8 0 0,0 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20M12.5,7V12.25L17,14.92L16.25,16.15L11,13V7H12.5Z",
        [IconCodes.Build] = "M2,20L20,2L22,4L4,22L2,20M16.5,6.5L17.5,7.5L7.5,17.5L6.5,16.5L16.5,6.5M15,1L13,3L21,11L23,9L15,1Z",

        [IconCodes.EyeOpen] = "M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z",
        [IconCodes.EyeOff] = "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z",

        [IconCodes.NavHome] = "M12,5.69L17,10.19V18H15V12H9V18H7V10.19L12,5.69M12,3L2,12H5V20H11V14H13V20H19V12H22L12,3Z",
        [IconCodes.NavNetwork] = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,19.93C7.05,19.44 4,16.08 4,12C4,11.38 4.08,10.78 4.21,10.21L9,15V16A2,2 0 0,0 11,18M17.9,17.39C17.64,16.59 16.89,16 16,16H15V13A1,1 0 0,0 14,12H8V10H10A1,1 0 0,0 11,9V7H13A2,2 0 0,0 15,5V4.59C17.93,5.77 20,8.64 20,12C20,14.08 19.2,15.97 17.9,17.39Z",
        [IconCodes.NavShare] = "M18,16.08C17.24,16.08 16.56,16.38 16.04,16.85L8.91,12.7C8.96,12.47 9,12.24 9,12C9,11.76 8.96,11.53 8.91,11.3L15.96,7.19C16.5,7.69 17.21,8 18,8A3,3 0 0,0 21,5A3,3 0 0,0 18,2A3,3 0 0,0 15,5C15,5.24 15.04,5.47 15.09,5.7L8.04,9.81C7.5,9.31 6.79,9 6,9A3,3 0 0,0 3,12A3,3 0 0,0 6,15C6.79,15 7.5,14.69 8.04,14.19L15.16,18.34C15.11,18.55 15.08,18.77 15.08,19C15.08,20.61 16.39,21.91 18,21.91C19.61,21.91 20.92,20.61 20.92,19A2.92,2.92 0 0,0 18,16.08Z",
        [IconCodes.NavSecurity] = "M12,1 L3,5 V11 C3,16.55 6.84,21.74 12,23 C17.16,21.74 21,16.55 21,11 V5 Z",
        [IconCodes.NavAdvanced] = "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z",
        [IconCodes.NavPrograms] = "M12.89,3L14.85,3.4L11.11,21L9.15,20.6L12.89,3M19.59,12L16,8.41V5.83L22.17,12L16,18.17V15.59L19.59,12M1.83,12L8,5.83V8.41L4.41,12L8,15.59V18.17L1.83,12Z",
        [IconCodes.NavLog] = "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,9V3.5L18.5,9H13M8,13H16V15H8V13M8,17H13V19H8V17Z",
        [IconCodes.NavAbout] = "M13,9H11V7H13M13,17H11V11H13M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z",
        [IconCodes.NavHelp] = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M13,19H11V17H13M15.07,11.25L14.17,12.17C13.45,12.89 13,13.5 13,15H11V14.5C11,13.39 11.45,12.39 12.17,11.67L13.41,10.41C13.78,10.05 14,9.55 14,9C14,7.89 13.1,7 12,7C10.9,7 10,7.89 10,9H8A4,4 0 0,1 12,5A4,4 0 0,1 16,9C16,10.1 15.55,11.05 14.07,12.25L15.07,11.25Z",
        [IconCodes.ChevronLeft] = "M15.41,16.58L10.83,12L15.41,7.41L14,6L8,12L14,18L15.41,16.58Z",
        [IconCodes.ChevronRight] = "M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z",

        [IconCodes.ViewCard] = "M3,3H10V10H3V3M14,3H21V10H14V3M3,14H10V21H3V14M14,14H21V21H14V14Z",
        [IconCodes.ViewList] = "M3,5H21V7H3V5M3,11H21V13H3V11M3,17H21V19H3V17Z",
        [IconCodes.ViewCanvas] = "M3,3H21V15H3V3M5,5V13H19V5H5M8,17H16V19H8V17M10,19H14V21H10V19Z",

        [IconCodes.SeekBack] = "M11.5,12L20,18V6M11,18V6L2.5,12L11,18Z",
        [IconCodes.SeekFwd] = "M13,6V18L21.5,12M4,18L12.5,12L4,6V18Z",
        [IconCodes.SeekFwd5] = "M12,5V1L17,6L12,11V7C8.69,7 6,9.69 6,13C6,16.31 8.69,19 12,19C15.31,19 18,16.31 18,13H20C20,17.42 16.42,21 12,21C7.58,21 4,17.42 4,13C4,8.58 7.58,5 12,5Z",
        [IconCodes.Next] = "M6,18L14.5,12L6,6V18M16,6V18H18V6H16Z",
        [IconCodes.Volume] = "M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.84 14,18.7V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.76 16.5,12M3,9V15H7L12,20V4L7,9H3Z",
        [IconCodes.Mute] = "M12,4L9.91,6.09L12,8.18M4.27,3L3,4.27L7.73,9H3V15H7L12,20V13.27L16.25,17.53C15.58,18.04 14.83,18.46 14,18.7V20.77C15.38,20.45 16.63,19.82 17.68,18.96L19.73,21L21,19.73L12,10.73M19,12C19,12.94 18.8,13.82 18.46,14.64L19.97,16.15C20.62,14.91 21,13.5 21,12C21,7.72 18,4.14 14,3.23V5.29C16.89,6.15 19,8.83 19,12M16.5,12C16.5,10.23 15.5,8.71 14,7.97V10.18L16.45,12.63C16.5,12.43 16.5,12.21 16.5,12Z",
        [IconCodes.Pip] = "M19,11H11V17H19V11M23,19V5C23,3.88 22.1,3 21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19M21,19H3V4.97H21V19Z",
        [IconCodes.Maximize] = "M4,4H20V20H4V4M6,8V18H18V8H6Z",
        [IconCodes.Fullscreen] = "M5,5H10V7H7V10H5V5M14,5H19V10H17V7H14V5M17,14H19V19H14V17H17V14M10,17V19H5V14H7V17H10Z",
        [IconCodes.FullscreenExit] = "M14,14H19V16H16V19H14V14M5,14H10V19H8V16H5V14M8,5H10V10H5V8H8V5M16,8V5H14V10H19V8H16Z",
        [IconCodes.PlayOutline] = "F0 M8,5 L19,12 L8,19 Z M10.4,9 L10.4,15 L15.5,12 Z",
        [IconCodes.PauseBars] = "M8,5H11V19H8V5M13,5H16V19H13V5Z",
        [IconCodes.StopSmall] = "M9,9H15V15H9V9Z"
    };

    /// <summary>兼容旧 Geo* 资源键 → 图标码。</summary>
    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.Ordinal)
    {
        ["GeoAdd"] = IconCodes.Add,
        ["GeoDelete"] = IconCodes.Delete,
        ["GeoEdit"] = IconCodes.Edit,
        ["GeoPlay"] = IconCodes.Play,
        ["GeoStop"] = IconCodes.Stop,
        ["GeoRefresh"] = IconCodes.Refresh,
        ["GeoDownload"] = IconCodes.Download,
        ["GeoCopy"] = IconCodes.Copy,
        ["GeoCalendar"] = IconCodes.Calendar,
        ["GeoScan"] = IconCodes.Scan,
        ["GeoHome"] = IconCodes.NavHome,
        ["GeoNetwork"] = IconCodes.NavNetwork,
        ["GeoShare"] = IconCodes.NavShare,
        ["GeoSecurity"] = IconCodes.NavSecurity,
        ["GeoAdvanced"] = IconCodes.NavAdvanced,
        ["GeoPrograms"] = IconCodes.NavPrograms,
        ["GeoLog"] = IconCodes.NavLog,
        ["GeoAbout"] = IconCodes.NavAbout,
        ["GeoHelp"] = IconCodes.NavHelp,
        ["GeoChevronLeft"] = IconCodes.ChevronLeft,
        ["GeoChevronRight"] = IconCodes.ChevronRight,
        ["GeoBack"] = IconCodes.ChevronLeft,
        ["GeoPause"] = IconCodes.Pause,
        ["GeoSettings"] = IconCodes.Settings,
        ["GeoSearch"] = IconCodes.Search,
        ["GeoCheck"] = IconCodes.Check,
        ["GeoClose"] = IconCodes.Close,
        ["GeoSave"] = IconCodes.Save,
        ["GeoClock"] = IconCodes.Clock,
        ["GeoBuild"] = IconCodes.Build,
        ["GeoFind"] = IconCodes.Search,
        ["GeoViewCard"] = IconCodes.ViewCard,
        ["GeoViewList"] = IconCodes.ViewList,
        ["GeoViewCanvas"] = IconCodes.ViewCanvas,
        ["GeoSeekBack"] = IconCodes.SeekBack,
        ["GeoSeekFwd"] = IconCodes.SeekFwd,
        ["GeoSeekFwd5"] = IconCodes.SeekFwd5,
        ["GeoNext"] = IconCodes.Next,
        ["GeoVolume"] = IconCodes.Volume,
        ["GeoMute"] = IconCodes.Mute,
        ["GeoPip"] = IconCodes.Pip,
        ["GeoMaximize"] = IconCodes.Maximize,
        ["GeoFullscreen"] = IconCodes.Fullscreen,
        ["GeoFullscreenExit"] = IconCodes.FullscreenExit,
        ["GeoEyeOpen"] = IconCodes.EyeOpen,
        ["GeoEyeOff"] = IconCodes.EyeOff,
        ["GeoPlayOutline"] = IconCodes.PlayOutline,
        ["GeoPauseBars"] = IconCodes.PauseBars,
        ["GeoStopSmall"] = IconCodes.StopSmall
    };

    public static IReadOnlyDictionary<string, string> AllPaths => Paths;

    public static IReadOnlyDictionary<string, string> AllLegacyAliases => LegacyAliases;

    public static bool TryGetPath(string codeOrLegacyKey, out string path)
    {
        if (Paths.TryGetValue(codeOrLegacyKey, out path!))
            return true;
        if (LegacyAliases.TryGetValue(codeOrLegacyKey, out var code) &&
            Paths.TryGetValue(code, out path!))
            return true;
        path = "";
        return false;
    }

    public static string GetPath(string codeOrLegacyKey)
        => TryGetPath(codeOrLegacyKey, out var path)
            ? path
            : throw new KeyNotFoundException($"Unknown icon code: {codeOrLegacyKey}");
}
