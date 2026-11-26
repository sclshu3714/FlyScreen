#if WINDOWS
namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>托盘二级/三级菜单项（可嵌套；可选行内 trailing 复选）。</summary>
internal readonly record struct MauiTraySubItem(
    string Label,
    Action? Action,
    byte[]? IconPng = null,
    string? Mdl2Glyph = null,
    IReadOnlyList<MauiTraySubItem>? Nested = null,
    string? TrailingCheckLabel = null,
    bool TrailingChecked = false,
    Action<bool>? TrailingCheckChanged = null)
{
    public bool IsSubmenu => Nested is { Count: > 0 };

    /// <summary>不可点的占位文案（如工具菜单为空）。</summary>
    public bool IsPlaceholder => Action == null && !IsSubmenu;

    public bool HasTrailingCheck =>
        !string.IsNullOrEmpty(TrailingCheckLabel) && TrailingCheckChanged != null;

    public static MauiTraySubItem Click(string label, Action action, byte[]? iconPng = null, string? mdl2 = null)
        => new(label, action, iconPng, mdl2);

    public static MauiTraySubItem ClickWithTrailingCheck(
        string label,
        Action action,
        string trailingCheckLabel,
        bool trailingChecked,
        Action<bool> trailingCheckChanged,
        string? mdl2 = null)
        => new(label, action, null, mdl2, null, trailingCheckLabel, trailingChecked, trailingCheckChanged);

    public static MauiTraySubItem Submenu(string label, IReadOnlyList<MauiTraySubItem> nested, string? mdl2 = null)
        => new(label, null, null, mdl2, nested);

    public static MauiTraySubItem Placeholder(string label)
        => new(label, null);
}
#endif
