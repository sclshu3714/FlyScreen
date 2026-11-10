namespace FlyScreen.Services.Tools;

/// <summary>本机工具安装进度（不进 BehaviorSync）。</summary>
public sealed class ToolInstallProgress
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _percentById = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public bool IsInstalling(string toolId)
    {
        lock (_gate)
            return _percentById.ContainsKey(toolId);
    }

    public int GetPercent(string toolId)
    {
        lock (_gate)
            return _percentById.TryGetValue(toolId, out var p) ? p : 0;
    }

    public void Begin(string toolId)
    {
        lock (_gate)
            _percentById[toolId] = 0;
        Raise();
    }

    public void Report(string toolId, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (_gate)
        {
            if (!_percentById.ContainsKey(toolId))
                return;
            _percentById[toolId] = percent;
        }

        Raise();
    }

    public void End(string toolId)
    {
        lock (_gate)
            _percentById.Remove(toolId);
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }
}
