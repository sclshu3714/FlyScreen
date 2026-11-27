namespace FlyScreen.ToolSdk;

/// <summary>
/// 可选：工具在宿主卸载前做数据处置。
/// 返回 <c>false</c> 表示用户取消，宿主 MUST NOT 继续卸载。
/// </summary>
public interface IToolUninstallPolicy
{
    /// <summary>
    /// 应用内卸载：通常拉 <see cref="GetUninstallUiSchema"/> → 宿主通用 UI → <see cref="ApplyUninstallAnswersAsync"/>。
    /// 无决策需求时可直接返回 true。
    /// </summary>
    Task<bool> PrepareUninstallAsync(CancellationToken ct = default);

    /// <summary>声明式决策表单；null 表示无需决策。应与包内 <c>uninstall-ui.json</c> 一致。</summary>
    ToolUninstallUiSchema? GetUninstallUiSchema();

    /// <summary>
    /// 按用户（或静默 defaults）答案执行数据处置。
    /// 返回 false 表示失败或拒绝继续卸载。
    /// </summary>
    Task<bool> ApplyUninstallAnswersAsync(
        IReadOnlyDictionary<string, string> answers,
        CancellationToken ct = default);
}
