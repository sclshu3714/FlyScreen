using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.PortProxy;

/// <summary>本机 netsh portproxy 会话服务（内嵌工作区；无跨屏）。</summary>
public sealed class PortProxyService : IPortProxyService
{
    private readonly object _gate = new();
    private bool _running;
    private IReadOnlyList<PortProxyRuleInfo> _rules = [];
    private string _lastStatus = "";
    private bool _elevateMutations = true;

    public event Action? SessionChanged;
    public event Action? RulesChanged;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public bool IsElevated => PortProxyNetsh.IsProcessElevated();

    /// <summary>变更类命令在未提升时是否弹出 UAC（默认 true）。</summary>
    public bool ElevateMutations
    {
        get { lock (_gate) return _elevateMutations; }
        set { lock (_gate) _elevateMutations = value; }
    }

    public IReadOnlyList<PortProxyRuleInfo> Rules
    {
        get { lock (_gate) return _rules; }
    }

    public string LastStatus
    {
        get { lock (_gate) return _lastStatus; }
    }

    public void Start()
    {
        lock (_gate)
        {
            _running = true;
            _lastStatus = "";
        }

        RaiseSession();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _rules = [];
            _lastStatus = "";
        }

        RaiseSession();
        RaiseRules();
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var rules = await PortProxyNetsh.ShowAllAsync(ct).ConfigureAwait(false);
        var infos = rules.Select(PortProxyMapping.ToInfo).ToList();
        lock (_gate)
        {
            _rules = infos;
            _lastStatus = $"已加载 {infos.Count} 条规则";
        }

        RaiseRules();
    }

    public Task<PortProxyOpResult> AddAsync(PortProxyRuleInfo rule, CancellationToken ct = default)
        => ExecuteMutationAsync(
            () => PortProxyNetsh.AddAsync(PortProxyMapping.ToRule(rule), ElevateMutations, ct), ct);

    public Task<PortProxyOpResult> SetAsync(PortProxyRuleInfo rule, CancellationToken ct = default)
        => ExecuteMutationAsync(
            () => PortProxyNetsh.SetAsync(PortProxyMapping.ToRule(rule), ElevateMutations, ct), ct);

    public Task<PortProxyOpResult> DeleteAsync(PortProxyRuleInfo rule, CancellationToken ct = default)
        => ExecuteMutationAsync(
            () => PortProxyNetsh.DeleteAsync(PortProxyMapping.ToRule(rule), ElevateMutations, ct), ct);

    public async Task<PortProxyOpResult> DeleteManyAsync(
        IReadOnlyList<PortProxyRuleInfo> rules, CancellationToken ct = default)
    {
        if (rules == null || rules.Count == 0)
            return PortProxyOpResult.Fail("未选择规则");

        var ok = 0;
        string lastFail = "";
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            var r = await PortProxyNetsh.DeleteAsync(PortProxyMapping.ToRule(rule), ElevateMutations, ct)
                .ConfigureAwait(false);
            if (r.Ok) ok++;
            else lastFail = r.Message;
        }

        await RefreshAsync(ct).ConfigureAwait(false);
        if (ok == rules.Count)
        {
            SetStatus($"已删除 {ok} 条");
            return PortProxyOpResult.Success($"已删除 {ok} 条");
        }

        var msg = $"删除完成 {ok}/{rules.Count}" + (string.IsNullOrWhiteSpace(lastFail) ? "" : " · " + lastFail);
        SetStatus(msg);
        return ok > 0 ? PortProxyOpResult.Success(msg) : PortProxyOpResult.Fail(msg);
    }

    public Task<PortProxyOpResult> ResetAsync(CancellationToken ct = default)
        => ExecuteMutationAsync(() => PortProxyNetsh.ResetAsync(ElevateMutations, ct), ct);

    public async Task<PortProxyOpResult> DumpAsync(CancellationToken ct = default)
    {
        var r = await PortProxyNetsh.DumpAsync(ct).ConfigureAwait(false);
        SetStatus(r.Ok ? "已导出 dump 脚本" : r.Message);
        return r.Ok
            ? PortProxyOpResult.Success(r.Message, r.StdOut)
            : PortProxyOpResult.Fail(r.Message);
    }

    private async Task<PortProxyOpResult> ExecuteMutationAsync(
        Func<Task<PortProxyCommandResult>> action, CancellationToken ct)
    {
        var r = await action().ConfigureAwait(false);
        SetStatus(r.Ok ? "操作成功" : r.Message);
        if (r.Ok)
            await RefreshAsync(ct).ConfigureAwait(false);
        return r.Ok
            ? PortProxyOpResult.Success(r.Message, r.StdOut)
            : PortProxyOpResult.Fail(r.Message);
    }

    private void SetStatus(string status)
    {
        lock (_gate) _lastStatus = status;
        RaiseSession();
    }

    private void RaiseSession()
    {
        try { SessionChanged?.Invoke(); }
        catch { /* ignore */ }
    }

    private void RaiseRules()
    {
        try { RulesChanged?.Invoke(); }
        catch { /* ignore */ }
    }
}

internal static class PortProxyMapping
{
    public static PortProxyRuleInfo ToInfo(PortProxyRule rule) => new()
    {
        Kind = (PortProxyMappingKind)(int)rule.Kind,
        ListenAddress = rule.ListenAddress,
        ListenPort = rule.ListenPort,
        ConnectAddress = rule.ConnectAddress,
        ConnectPort = rule.ConnectPort,
        Protocol = rule.Protocol
    };

    public static PortProxyRule ToRule(PortProxyRuleInfo info) => new()
    {
        Kind = (PortProxyKind)(int)info.Kind,
        ListenAddress = info.ListenAddress,
        ListenPort = info.ListenPort,
        ConnectAddress = info.ConnectAddress,
        ConnectPort = info.ConnectPort,
        Protocol = info.Protocol
    };
}
