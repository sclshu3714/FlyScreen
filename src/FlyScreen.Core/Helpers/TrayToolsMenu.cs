using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;

namespace FlyScreen.Helpers;

/// <summary>托盘「工具」二级菜单：显示工具页快捷栏已固定项。</summary>
public static class TrayToolsMenu
{
    public static string EmptyLabel => Loc.T("Tray.ToolsEmpty");

    /// <summary>快捷栏占用项（Id + 显示名）。无快捷时返回空列表。</summary>
    public static IReadOnlyList<(string ToolId, string Label)> BuildItems()
    {
        var occupied = ToolService.Instance.Shortcuts.Occupied;
        if (occupied.Count == 0)
            return [];

        var rows = ToolCardRows.Build();
        var list = new List<(string, string)>(occupied.Count);
        foreach (var id in occupied)
        {
            var row = rows.FirstOrDefault(r =>
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            list.Add((id, row?.Name ?? id));
        }

        return list;
    }

    /// <summary>解析快捷栏工具对应卡片；找不到时返回 null。</summary>
    public static ToolCardRow? ResolveRow(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;
        return ToolCardRows.Build().FirstOrDefault(r =>
            string.Equals(r.Id, toolId, StringComparison.OrdinalIgnoreCase));
    }
}
