namespace FlyScreen.ToolSdk;

/// <summary>端口代理规则方向（与 netsh v4tov4/v4tov6/v6tov4/v6tov6 对齐）。</summary>
public enum PortProxyMappingKind
{
    V4ToV4,
    V4ToV6,
    V6ToV4,
    V6ToV6
}

/// <summary>一条 portproxy 规则（契约 DTO，供 UI 与工具包跨 ALC 共用）。</summary>
public sealed class PortProxyRuleInfo
{
    public PortProxyMappingKind Kind { get; init; }
    public string ListenAddress { get; init; } = "";
    public string ListenPort { get; init; } = "";
    public string ConnectAddress { get; init; } = "";
    public string ConnectPort { get; init; } = "";
    public string Protocol { get; init; } = "tcp";

    public string KindToken => Kind switch
    {
        PortProxyMappingKind.V4ToV4 => "v4tov4",
        PortProxyMappingKind.V4ToV6 => "v4tov6",
        PortProxyMappingKind.V6ToV4 => "v6tov4",
        PortProxyMappingKind.V6ToV6 => "v6tov6",
        _ => "v4tov4"
    };

    public PortProxyRuleInfo Clone() => new()
    {
        Kind = Kind,
        ListenAddress = ListenAddress,
        ListenPort = ListenPort,
        ConnectAddress = ConnectAddress,
        ConnectPort = ConnectPort,
        Protocol = string.IsNullOrWhiteSpace(Protocol) ? "tcp" : Protocol
    };
}

/// <summary>netsh 调用结果。</summary>
public sealed class PortProxyOpResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string StdOut { get; init; } = "";

    public static PortProxyOpResult Success(string message = "", string stdout = "")
        => new() { Ok = true, Message = message, StdOut = stdout };

    public static PortProxyOpResult Fail(string message)
        => new() { Ok = false, Message = message };
}

/// <summary>端口代理服务契约（实现位于工具包；UI 经宿主 GetService 取用）。</summary>
public interface IPortProxyService
{
    event Action? SessionChanged;
    event Action? RulesChanged;

    bool IsRunning { get; }
    bool IsElevated { get; }
    string LastStatus { get; }
    IReadOnlyList<PortProxyRuleInfo> Rules { get; }

    void Start();
    void Stop();
    Task RefreshAsync(CancellationToken ct = default);
    Task<PortProxyOpResult> AddAsync(PortProxyRuleInfo rule, CancellationToken ct = default);
    Task<PortProxyOpResult> SetAsync(PortProxyRuleInfo rule, CancellationToken ct = default);
    Task<PortProxyOpResult> DeleteAsync(PortProxyRuleInfo rule, CancellationToken ct = default);
    Task<PortProxyOpResult> DeleteManyAsync(IReadOnlyList<PortProxyRuleInfo> rules, CancellationToken ct = default);
    Task<PortProxyOpResult> ResetAsync(CancellationToken ct = default);
    Task<PortProxyOpResult> DumpAsync(CancellationToken ct = default);
}
