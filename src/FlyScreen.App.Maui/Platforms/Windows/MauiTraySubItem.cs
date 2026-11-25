#if WINDOWS
namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>托盘二级/三级菜单项（可嵌套）。</summary>
internal readonly record struct MauiTraySubItem(
    string Label,
    Action? Action,
    byte[]? IconPng = null,
    string? Mdl2Glyph = null,
    IReadOnlyList<MauiTraySubItem>? Nested = null)
{
    public bool IsSubmenu => Nested is { Count: > 0 };

    public static MauiTraySubItem Click(string label, Action action, byte[]? iconPng = null, string? mdl2 = null)
        => new(label, action, iconPng, mdl2);

    public static MauiTraySubItem Submenu(string label, IReadOnlyList<MauiTraySubItem> nested, string? mdl2 = null)
        => new(label, null, null, mdl2, nested);
}
#endif
