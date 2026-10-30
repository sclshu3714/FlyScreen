using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

public enum ProjectionTargetKind
{
    /// <summary>本机已有非主物理/已枚举副屏（扩展时可直接采集，不强制 MTT）。</summary>
    LocalSecondaryMonitor = 0,
    /// <summary>双向已连接的电脑/大屏。</summary>
    Peer = 1
}

/// <summary>投影目标：真副屏或对端设备。</summary>
public sealed class ProjectionTarget
{
    public ProjectionTargetKind Kind { get; init; }
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>副屏时为监视器描述；Peer 时为机器名。</summary>
    public string? PeerName => Kind == ProjectionTargetKind.Peer ? Id : null;

    public override string ToString() => DisplayName;
}

public static class ProjectionTargets
{
    /// <summary>
    /// 目标列表：本机副屏（可选）+ 双向已连接电脑/大屏。
    /// <paramref name="localSecondaryLabels"/> 由平台枚举后传入（Core 不依赖 Win32）。
    /// </summary>
    public static IReadOnlyList<ProjectionTarget> ListForPicker(
        ConnectionManager connections,
        IReadOnlyList<(string Id, string Label)>? localSecondaryLabels = null,
        string? preferPeer = null)
    {
        var list = new List<ProjectionTarget>();

        if (localSecondaryLabels != null)
        {
            foreach (var (id, label) in localSecondaryLabels)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new ProjectionTarget
                {
                    Kind = ProjectionTargetKind.LocalSecondaryMonitor,
                    Id = id.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(label) ? $"本机副屏 ({id})" : label.Trim()
                });
            }
        }

        foreach (var name in ScreenExtendTargets.ListEligible(connections))
        {
            list.Add(new ProjectionTarget
            {
                Kind = ProjectionTargetKind.Peer,
                Id = name,
                DisplayName = name
            });
        }

        // 保留上次选择的对端（即使暂未双向），避免列表抖动
        if (!string.IsNullOrWhiteSpace(preferPeer)
            && list.All(t => !string.Equals(t.Id, preferPeer, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var name in ScreenExtendTargets.ListForPicker(connections, preferPeer))
            {
                if (list.Any(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new ProjectionTarget
                {
                    Kind = ProjectionTargetKind.Peer,
                    Id = name,
                    DisplayName = name + "（未双向）"
                });
            }
        }

        return list;
    }
}

/// <summary>开始投影结果。</summary>
public sealed record ProjectionStartResult(bool Ok, bool NeedEnvironmentRepair, string? Message)
{
    public static ProjectionStartResult Success(string? message = null)
        => new(true, false, message);

    public static ProjectionStartResult Fail(string message)
        => new(false, false, message);

    public static ProjectionStartResult NeedRepair(string message)
        => new(false, true, message);
}
