namespace FlyScreen.ToolSdk;

/// <summary>卡片上下文（与宿主 ToolCardRow 字段对齐，避免 SDK 依赖 Core UI 模型）。</summary>
public sealed class ToolCardInfo
{
    public required string Id { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsInstalling { get; init; }
    public bool IsRunning { get; init; }
    public bool CanStart { get; init; }
    public bool CanConsume { get; init; }
    public bool CanProvide { get; init; }
    public bool HasWorkspace { get; init; }
}

public sealed class ToolCardContext
{
    public required ToolCardInfo Row { get; init; }
}

public sealed class ToolMenuContribution
{
    public required string ActionId { get; init; }
    public required string Label { get; init; }
}
