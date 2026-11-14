namespace FlyScreen.ToolSdk;

/// <summary>
/// 工具平台 UI 贡献：实现位于工具 UI 程序集（非主程序）。
/// 宿主只扫描并 Register，不按 ToolId 写分支。
/// </summary>
public interface IToolUiContributor
{
    void Register(IToolUiHost ui, IToolUiContext context);
}
