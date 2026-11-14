namespace FlyScreen.ToolSdk;

/// <summary>
/// 工具多语言贡献：由宿主 <c>Loc.RegisterPlugin</c> 接入。
/// 工具包 MUST 自带文案表，不得依赖主程序新增键。
/// </summary>
public interface IToolLocalization
{
    /// <summary>与 ToolId 对齐的插件源 Id。</summary>
    string SourceId { get; }

    /// <summary>culture → (key → text)。至少含 zh-CN 与 en-US。</summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; }
}
