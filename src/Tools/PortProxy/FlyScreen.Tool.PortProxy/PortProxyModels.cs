namespace FlyScreen.Tool.PortProxy;

/// <summary>portproxy 映射方向（对应 netsh add/delete/set/show 子命令）。</summary>
public enum PortProxyKind
{
    V4ToV4,
    V4ToV6,
    V6ToV4,
    V6ToV6
}

public static class PortProxyKindExtensions
{
    public static string ToNetshToken(this PortProxyKind kind) => kind switch
    {
        PortProxyKind.V4ToV4 => "v4tov4",
        PortProxyKind.V4ToV6 => "v4tov6",
        PortProxyKind.V6ToV4 => "v6tov4",
        PortProxyKind.V6ToV6 => "v6tov6",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool TryParse(string? token, out PortProxyKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        switch (token.Trim().ToLowerInvariant())
        {
            case "v4tov4": kind = PortProxyKind.V4ToV4; return true;
            case "v4tov6": kind = PortProxyKind.V4ToV6; return true;
            case "v6tov4": kind = PortProxyKind.V6ToV4; return true;
            case "v6tov6": kind = PortProxyKind.V6ToV6; return true;
            default: return false;
        }
    }

    public static IReadOnlyList<PortProxyKind> All { get; } =
    [
        PortProxyKind.V4ToV4,
        PortProxyKind.V4ToV6,
        PortProxyKind.V6ToV4,
        PortProxyKind.V6ToV6
    ];
}

/// <summary>一条 portproxy 规则。</summary>
public sealed class PortProxyRule
{
    public PortProxyKind Kind { get; init; }
    public string ListenAddress { get; init; } = "";
    public string ListenPort { get; init; } = "";
    public string ConnectAddress { get; init; } = "";
    public string ConnectPort { get; init; } = "";
    public string Protocol { get; init; } = "tcp";

    public string KindToken => Kind.ToNetshToken();

    public PortProxyRule Clone() => new()
    {
        Kind = Kind,
        ListenAddress = ListenAddress,
        ListenPort = ListenPort,
        ConnectAddress = ConnectAddress,
        ConnectPort = ConnectPort,
        Protocol = string.IsNullOrWhiteSpace(Protocol) ? "tcp" : Protocol
    };

    public bool SameListenKey(PortProxyRule other)
        => Kind == other.Kind
           && string.Equals(NormalizeAddr(ListenAddress), NormalizeAddr(other.ListenAddress), StringComparison.OrdinalIgnoreCase)
           && string.Equals(ListenPort?.Trim(), other.ListenPort?.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(Protocol?.Trim(), other.Protocol?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAddr(string? addr)
        => string.IsNullOrWhiteSpace(addr) ? "*" : addr.Trim();
}

public sealed class PortProxyCommandResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public int ExitCode { get; init; }

    public static PortProxyCommandResult Success(string message = "", string stdout = "")
        => new() { Ok = true, Message = message, StdOut = stdout, ExitCode = 0 };

    public static PortProxyCommandResult Fail(string message, int exitCode = -1, string stdout = "", string stderr = "")
        => new() { Ok = false, Message = message, ExitCode = exitCode, StdOut = stdout, StdErr = stderr };
}
