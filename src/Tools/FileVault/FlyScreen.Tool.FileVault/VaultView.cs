using FlyScreen.Helpers.Localization;

namespace FlyScreen.Tool.FileVault;

public enum VaultLayoutMode
{
    ExtraLargeIcons,
    LargeIcons,
    MediumIcons,
    SmallIcons,
    List,
    Details,
    Tiles,
    Content
}

public enum VaultSidePane
{
    None,
    Details,
    Preview
}

public sealed class VaultViewState
{
    public VaultLayoutMode Layout { get; set; } = VaultLayoutMode.Details;
    public VaultSidePane SidePane { get; set; } = VaultSidePane.None;
    public bool NavigationPane { get; set; }
    public bool CompactView { get; set; }
    public bool ItemCheckBoxes { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public bool ShowHidden { get; set; } = true;
}

/// <summary>对齐资源管理器的「查看」：布局、窗格与显示开关。</summary>
public static class VaultView
{
    public static readonly VaultLayoutMode[] Layouts =
    [
        VaultLayoutMode.ExtraLargeIcons,
        VaultLayoutMode.LargeIcons,
        VaultLayoutMode.MediumIcons,
        VaultLayoutMode.SmallIcons,
        VaultLayoutMode.List,
        VaultLayoutMode.Details,
        VaultLayoutMode.Tiles,
        VaultLayoutMode.Content
    ];

    public static string Title(VaultLayoutMode mode) => mode switch
    {
        VaultLayoutMode.ExtraLargeIcons => Loc.T("Tool.FileVault.ViewExtraLarge"),
        VaultLayoutMode.LargeIcons => Loc.T("Tool.FileVault.ViewLarge"),
        VaultLayoutMode.MediumIcons => Loc.T("Tool.FileVault.ViewMedium"),
        VaultLayoutMode.SmallIcons => Loc.T("Tool.FileVault.ViewSmall"),
        VaultLayoutMode.List => Loc.T("Tool.FileVault.ViewList"),
        VaultLayoutMode.Details => Loc.T("Tool.FileVault.ViewDetails"),
        VaultLayoutMode.Tiles => Loc.T("Tool.FileVault.ViewTiles"),
        VaultLayoutMode.Content => Loc.T("Tool.FileVault.ViewContent"),
        _ => ""
    };

    public static string IconCode(VaultLayoutMode mode) => mode switch
    {
        VaultLayoutMode.ExtraLargeIcons => "icon.view.extra-large",
        VaultLayoutMode.LargeIcons => "icon.view.large",
        VaultLayoutMode.MediumIcons => "icon.view.medium",
        VaultLayoutMode.SmallIcons => "icon.view.small",
        VaultLayoutMode.List => "icon.view.list",
        VaultLayoutMode.Details => "icon.view.details",
        VaultLayoutMode.Tiles => "icon.view.tiles",
        VaultLayoutMode.Content => "icon.view.content",
        _ => "icon.view.details"
    };

    public static string DetailsPaneIcon => "icon.view.details-pane";
    public static string PreviewPaneIcon => "icon.view.preview-pane";
    public static string ShowIcon => "icon.view.show";
    public static string NavPaneIcon => "icon.view.nav-pane";
    public static string CompactIcon => "icon.view.compact";
    public static string CheckBoxesIcon => "icon.view.checkboxes";
    public static string ExtensionsIcon => "icon.view.extensions";
    public static string HiddenIcon => "icon.view.hidden";

    public static double CellWidth(VaultLayoutMode mode) => IconSize(mode) + (mode == VaultLayoutMode.Tiles ? 72 : 40);

    public static double IconSize(VaultLayoutMode mode) => mode switch
    {
        VaultLayoutMode.ExtraLargeIcons => 96,
        VaultLayoutMode.LargeIcons => 72,
        VaultLayoutMode.MediumIcons => 48,
        VaultLayoutMode.SmallIcons => 32,
        VaultLayoutMode.Tiles => 48,
        VaultLayoutMode.Content => 36,
        _ => 16
    };

    public static int GridSpan(VaultLayoutMode mode) => mode switch
    {
        VaultLayoutMode.ExtraLargeIcons => 2,
        VaultLayoutMode.LargeIcons => 3,
        VaultLayoutMode.MediumIcons => 4,
        VaultLayoutMode.SmallIcons => 6,
        VaultLayoutMode.Tiles => 3,
        _ => 1
    };

    public static bool IsIconLayout(VaultLayoutMode mode) =>
        mode is VaultLayoutMode.ExtraLargeIcons or VaultLayoutMode.LargeIcons
            or VaultLayoutMode.MediumIcons or VaultLayoutMode.SmallIcons
            or VaultLayoutMode.Tiles;

    public static double RowPadding(VaultViewState state) =>
        state.CompactView ? 4 : (state.Layout == VaultLayoutMode.Content ? 10 : 8);

    public static string DisplayName(string name, bool isDir, bool showExtensions)
    {
        if (isDir || showExtensions || string.IsNullOrEmpty(name))
            return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrEmpty(stem) ? name : stem;
    }

    public static void ToggleSidePane(VaultViewState state, VaultSidePane pane)
        => state.SidePane = state.SidePane == pane ? VaultSidePane.None : pane;
}
