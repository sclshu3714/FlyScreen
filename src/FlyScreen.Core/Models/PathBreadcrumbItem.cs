namespace FlyScreen.Models;

/// <summary>
/// 资源管理器风格路径条段（根→当前）。Id 为 null 表示虚拟根。
/// </summary>
public sealed class PathBreadcrumbItem
{
    public string? Id { get; init; }
    public required string Title { get; init; }
    /// <summary>该段所在父级 Id（根的 ParentId 为 null）；› 菜单列出此父下同级文件夹。</summary>
    public string? ParentId { get; init; }
}
