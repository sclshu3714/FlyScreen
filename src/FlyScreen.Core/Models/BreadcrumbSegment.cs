namespace FlyScreen.Models;

/// <summary>
/// 跨端面包屑段：UI 用同一视觉约定渲染（主题色 Blue / BlueSoft / Text / Muted）。
/// </summary>
public sealed class BreadcrumbSegment
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public bool IsActive { get; init; }
    public bool IsClickable { get; init; } = true;
    /// <summary>可选：工具工作区关闭 Tab。</summary>
    public bool ShowClose { get; init; }
}
