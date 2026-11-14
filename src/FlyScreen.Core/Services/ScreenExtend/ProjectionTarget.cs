using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

public enum ProjectionTargetKind
{
    /// <summary>本机副屏（扩展模式遗留；主窗投影不再混入）。</summary>
    LocalSecondaryMonitor = 0,
    /// <summary>电脑布局中的局域网电脑/大屏。</summary>
    Peer = 1
}

/// <summary>主窗投影目标（非设备管理远程投影）。</summary>
public sealed class ProjectionTarget
{
    public ProjectionTargetKind Kind { get; init; }
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";

    public string? PeerName => Kind == ProjectionTargetKind.Peer ? Id : null;

    public static ProjectionTarget Peer(string peerName, string? displayName = null)
    {
        var name = (peerName ?? "").Trim();
        return new ProjectionTarget
        {
            Kind = ProjectionTargetKind.Peer,
            Id = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim()
        };
    }

    public override string ToString() => DisplayName;
}

public static class ProjectionTargets
{
    /// <summary>
    /// 主窗「投影」目标列表：仅电脑布局局域网电脑（见 <see cref="ScreenExtendTargets"/>）。
    /// 与设备管理「远程/中转投影」无关；勿传入远程名作 prefer 期望强行入列。
    /// </summary>
    public static IReadOnlyList<ProjectionTarget> ListForPicker(
        ConnectionManager connections,
        IReadOnlyList<(string Id, string Label)>? localSecondaryLabels = null,
        string? preferPeer = null)
    {
        _ = localSecondaryLabels; // 主窗投影不列本机副屏

        var names = ScreenExtendTargets.ListForPicker(connections, preferPeer);
        var list = new List<ProjectionTarget>(names.Count);
        foreach (var name in names)
        {
            var st = connections.GetPeerStatus(name);
            var label = st.AnyAlive ? name : name + "（未连接）";
            list.Add(ProjectionTarget.Peer(name, label));
        }

        // prefer 仅用于调用方选中，不在此插入布局外项
        return list;
    }
}

public sealed record ProjectionStartResult(bool Ok, bool NeedEnvironmentRepair, string? Message)
{
    public static ProjectionStartResult Success(string? message = null)
        => new(true, false, message);

    public static ProjectionStartResult Fail(string message)
        => new(false, false, message);

    public static ProjectionStartResult NeedRepair(string message)
        => new(false, true, message);
}
