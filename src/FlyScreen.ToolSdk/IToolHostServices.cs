namespace FlyScreen.ToolSdk;

/// <summary>
/// 宿主白名单服务。工具通过 <see cref="GetService{T}"/> 取连接/设置等；
/// MUST NOT 暴露无障碍或键鼠注入类型。
/// </summary>
public interface IToolHostServices
{
    IToolUiHost? Ui { get; set; }

    IToolWorkspace Workspace { get; }

    T? GetService<T>() where T : class;

    /// <summary>工具模块向宿主注册服务（如 <c>ISharedDevicesRuntime</c>）。</summary>
    void RegisterService<T>(T instance) where T : class;

    /// <summary>卸载时取消注册，避免宿主字典钉住工具程序集导致无法删 DLL。</summary>
    void UnregisterService<T>() where T : class;
}

/// <summary>工具工作区页签。</summary>
public interface IToolWorkspace
{
    void OpenOrFocus(string toolId, string title);
    void Close(string toolId);
    void Activate(string toolId);
    void ShowCatalog();
    bool HasOpenTabs { get; }
    bool IsShowingCatalog { get; }
    string? ActiveToolId { get; }
}

/// <summary>工具 → UI 请求通道。</summary>
public interface IToolUiHost
{
    void RegisterHandler(string toolId, string kind, Func<object?, CancellationToken, Task<object?>> handler);

    void RegisterWorkspaceFactory(string toolId, Func<object> factory);

    /// <summary>移除该工具的 UI 处理器与工作区工厂（卸载前调用）。</summary>
    void UnregisterTool(string toolId);

    Task<object?> RequestAsync(string toolId, string kind, object? argument = null, CancellationToken ct = default);

    object? CreateWorkspaceContent(string toolId);
}
