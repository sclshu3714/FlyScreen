namespace FlyScreen.Services.Tools;

public enum ToolImportKind
{
    FlyTool,
    LocalProgram
}

/// <summary>导入探测结果；有冲突时宿主先询问覆盖/放弃，再 <see cref="ToolService.CommitImportAsync"/>。</summary>
public sealed class ToolImportPlan
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public ToolImportKind Kind { get; init; }
    public string SourcePath { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool HasConflict { get; init; }
    public string ConflictDetail { get; init; } = "";

    public static ToolImportPlan Fail(string message)
        => new() { Ok = false, Message = message };
}
