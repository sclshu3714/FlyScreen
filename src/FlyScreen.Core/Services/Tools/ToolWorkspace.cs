namespace FlyScreen.Services.Tools;

/// <summary>工具页内嵌工作区的一个页签（不含「工具」目录项）。</summary>
public sealed class ToolWorkspaceTab
{
    public required string ToolId { get; init; }
    public required string Title { get; init; }
}

/// <summary>
/// 工具页内嵌宿主状态：目录 + 已打开工具页签。
/// UI 订阅 <see cref="Changed"/> 刷新导航条与内容区。
/// </summary>
public sealed class ToolWorkspace
{
    private readonly object _gate = new();
    private readonly List<ToolWorkspaceTab> _tabs = [];
    /// <summary>null = 显示目录；否则为当前工具页 ToolId。</summary>
    private string? _activeToolId;

    public event Action? Changed;

    public IReadOnlyList<ToolWorkspaceTab> OpenTabs
    {
        get { lock (_gate) return _tabs.ToList(); }
    }

    public bool HasOpenTabs
    {
        get { lock (_gate) return _tabs.Count > 0; }
    }

    public bool IsShowingCatalog
    {
        get { lock (_gate) return string.IsNullOrEmpty(_activeToolId); }
    }

    public string? ActiveToolId
    {
        get { lock (_gate) return _activeToolId; }
    }

    public bool IsOpen(string toolId)
    {
        lock (_gate)
            return _tabs.Any(t => string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
    }

    public void OpenOrFocus(string toolId, string title)
    {
        lock (_gate)
        {
            var hit = _tabs.FirstOrDefault(t =>
                string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                _tabs.Add(new ToolWorkspaceTab { ToolId = toolId, Title = title });
            _activeToolId = toolId;
        }

        Changed?.Invoke();
    }

    public void ShowCatalog()
    {
        lock (_gate) _activeToolId = null;
        Changed?.Invoke();
    }

    public void Activate(string? toolId)
    {
        if (string.IsNullOrEmpty(toolId))
        {
            ShowCatalog();
            return;
        }

        lock (_gate)
        {
            if (!_tabs.Any(t => string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase)))
                return;
            _activeToolId = toolId;
        }

        Changed?.Invoke();
    }

    public bool Close(string toolId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _tabs.RemoveAll(t =>
                string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed
                && string.Equals(_activeToolId, toolId, StringComparison.OrdinalIgnoreCase))
            {
                _activeToolId = _tabs.Count > 0 ? _tabs[^1].ToolId : null;
            }
        }

        if (removed)
            Changed?.Invoke();
        return removed;
    }

    public void CloseAll()
    {
        lock (_gate)
        {
            if (_tabs.Count == 0 && _activeToolId == null) return;
            _tabs.Clear();
            _activeToolId = null;
        }

        Changed?.Invoke();
    }
}
