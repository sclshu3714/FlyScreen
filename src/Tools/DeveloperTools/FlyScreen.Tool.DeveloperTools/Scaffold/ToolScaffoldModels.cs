namespace FlyScreen.Tool.DeveloperTools.Scaffold;

/// <summary>生成工具的界面形态。</summary>
public enum ToolUiMode
{
    /// <summary>嵌入工具页工作区（hasWorkspace）。</summary>
    Workspace = 0,
    /// <summary>启动即弹窗，无嵌入主界面。</summary>
    Dialog = 1,
    /// <summary>纯后台：启动运行、停止结束，无 UI。</summary>
    Background = 2
}

/// <summary>脚手架生成请求（表单绑定）。</summary>
public sealed class ToolScaffoldRequest
{
    /// <summary>中文显示名（manifest Name / Loc）。</summary>
    public string DisplayNameZh { get; set; } = "";

    /// <summary>英文项目名（PascalCase，如 SampleTool）。</summary>
    public string ProjectNameEn { get; set; } = "";

    /// <summary>一句话功能说明（中文）。</summary>
    public string SummaryZh { get; set; } = "";

    /// <summary>英文说明（可空，默认由中文占位）。</summary>
    public string SummaryEn { get; set; } = "";

    public bool SupportDesktop { get; set; } = true;
    public bool SupportPhone { get; set; }

    public ToolUiMode UiMode { get; set; } = ToolUiMode.Workspace;

    /// <summary>
    /// 输出根目录。默认 <c>%AppData%/FlyScreen/Code</c>（安装包可用）；
    /// 源码贡献者可改选仓库 <c>src/Tools</c>。最终工程在 {OutputRoot}/{ProjectNameEn}/。
    /// </summary>
    public string OutputRoot { get; set; } = "";

    /// <summary>是否尝试写入仓库 FlyScreen.sln / Pack-FlyTools / ToolIds（仅当输出落在本仓库 src/Tools 下）。</summary>
    public bool RegisterInRepo { get; set; }
}

public sealed class ToolScaffoldResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string? ProjectFolder { get; init; }
    public string? ToolId { get; init; }
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];
}

public sealed class ToolScaffoldIdentity
{
    public required string ToolId { get; init; }
    public required string FolderName { get; init; }
    public required string AssemblyName { get; init; }
    public required string Namespace { get; init; }
    public required string LocPrefix { get; init; }
    public required string IconKey { get; init; }
    public required string NameKey { get; init; }
    public required string SummaryKey { get; init; }
}
