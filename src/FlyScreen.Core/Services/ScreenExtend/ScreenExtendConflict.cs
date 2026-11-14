namespace FlyScreen.Services.ScreenExtend;

/// <summary>投影互斥：本机 Host 单路；Sink 可多路。Host 忙时不能再收/发。</summary>
public static class ScreenExtendConflict
{
    public const string ReasonBusyHost = "busy-host";
    public const string ReasonBusySink = "busy-sink";
    public const string ReasonBusy = "busy"; // 旧端
    public const string ReasonDeclined = "declined";

    public static string ReasonForRole(ScreenExtendRole role)
        => role == ScreenExtendRole.Host ? ReasonBusyHost
            : role == ScreenExtendRole.Sink ? ReasonBusySink
            : ReasonBusy;

    /// <summary>本机已有会话时，发起/接收的本地提示。</summary>
    public static string DescribeLocalBusy(ScreenExtendRole role, string? peer)
    {
        if (role == ScreenExtendRole.Host)
        {
            return string.IsNullOrWhiteSpace(peer)
                ? "本机正在投影中，请先停止后再发起或接收其他投影。"
                : $"本机正在向「{peer}」投影，请先停止后再发起或接收其他投影。";
        }

        if (role == ScreenExtendRole.Sink)
        {
            return string.IsNullOrWhiteSpace(peer)
                ? "本机正在接收投影，无法再作为投影主机。请先结束接收后再试。"
                : $"本机正在接收「{peer}」等投影，无法再作为投影主机。请先结束接收后再试。";
        }

        return "本机已有投影会话，请先结束后再试。";
    }

    /// <summary>对端拒绝 Offer 时，发起端看到的提示。</summary>
    public static string DescribeRemoteReject(string peer, string? reason)
    {
        var name = string.IsNullOrWhiteSpace(peer) ? "对端" : peer.Trim();
        return (reason ?? "").Trim().ToLowerInvariant() switch
        {
            ReasonBusyHost => $"「{name}」正在投影中，无法再作为接收端。请待其停止投影后再试。",
            ReasonBusySink => $"「{name}」正在接收其他投影，无法再作为目标。请待其结束接收后再试。",
            ReasonBusy => $"「{name}」正忙于投影会话，无法再作为目标。",
            ReasonDeclined => $"「{name}」拒绝了本次投影。",
            "" => $"「{name}」拒绝了本次投影。",
            _ => $"「{name}」拒绝了本次投影（{reason}）。"
        };
    }

    /// <summary>本机拒绝别人 Offer 时的状态栏说明。</summary>
    public static string DescribeLocalRejectOffer(string fromPeer, ScreenExtendRole localRole, string? localPeer)
    {
        var from = string.IsNullOrWhiteSpace(fromPeer) ? "对端" : fromPeer.Trim();
        return $"投影冲突：已拒绝「{from}」的发起。{DescribeLocalBusy(localRole, localPeer)}";
    }
}
