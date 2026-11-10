namespace FlyScreen.ToolSdk;

/// <summary>可安装工具模块契约。宿主只认此接口，不认具体工具类型。</summary>
public interface IToolModule
{
    string Id { get; }

    bool IsRunning { get; }

    bool UsesWorkspace { get; }

    event Action? StateChanged;

    Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default);

    Task OnUnloadedAsync(CancellationToken ct = default);

    Task<(bool Ok, string Message)> PrimaryAsync(ToolCardContext ctx, bool fromMenu, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardContext ctx);

    Task InvokeMenuAsync(string actionId, CancellationToken ct = default);
}
