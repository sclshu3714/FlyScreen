using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools.Hosting;

/// <summary>默认 UI 桥：字典注册（实现 SDK <see cref="IToolUiHost"/>）。</summary>
public sealed class ToolUiHost : IToolUiHost
{
    private readonly Dictionary<(string ToolId, string Kind), Func<object?, CancellationToken, Task<object?>>> _handlers =
        new(ToolIdKindComparer.Instance);

    private readonly Dictionary<string, Func<object>> _workspaces =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterHandler(string toolId, string kind, Func<object?, CancellationToken, Task<object?>> handler)
    {
        if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("toolId/kind required");
        _handlers[(toolId.Trim(), kind.Trim())] = handler
            ?? throw new ArgumentNullException(nameof(handler));
    }

    public void RegisterWorkspaceFactory(string toolId, Func<object> factory)
    {
        if (string.IsNullOrWhiteSpace(toolId))
            throw new ArgumentException("toolId required");
        _workspaces[toolId.Trim()] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public void UnregisterTool(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        var id = toolId.Trim();
        _workspaces.Remove(id);
        foreach (var key in _handlers.Keys.Where(k =>
                     string.Equals(k.ToolId, id, StringComparison.OrdinalIgnoreCase)).ToList())
            _handlers.Remove(key);
    }

    /// <summary>重新发现贡献者前清空，避免错误 UI 栈的工厂残留。</summary>
    public void ClearAll()
    {
        _handlers.Clear();
        _workspaces.Clear();
    }

    public async Task<object?> RequestAsync(
        string toolId, string kind, object? argument = null, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue((toolId, kind), out var handler))
            return null;
        return await handler(argument, ct).ConfigureAwait(true);
    }

    public object? CreateWorkspaceContent(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;
        if (!_workspaces.TryGetValue(toolId.Trim(), out var factory))
            return null;
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"创建工具工作区失败 {toolId}: {ex.Message}");
            return null;
        }
    }

    private sealed class ToolIdKindComparer : IEqualityComparer<(string ToolId, string Kind)>
    {
        public static ToolIdKindComparer Instance { get; } = new();

        public bool Equals((string ToolId, string Kind) x, (string ToolId, string Kind) y)
            => string.Equals(x.ToolId, y.ToolId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ToolId, string Kind) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ToolId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind));
    }
}
