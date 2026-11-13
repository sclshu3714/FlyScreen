using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>播放器「+」可选投影源：已连接/已发现设备，排除本机与已在 Tab 中的对端。</summary>
public static class ProjectionAddTargets
{
    public static List<string> List(
        ConnectionManager connections,
        IEnumerable<string>? discoveredNames,
        IEnumerable<string> excludePeers)
    {
        var local = LocalPeerIdentity.MachineName;
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { local };
        foreach (var p in excludePeers)
        {
            if (!string.IsNullOrWhiteSpace(p))
                exclude.Add(p.Trim());
        }

        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in connections.GetConfiguredPeerStatuses())
        {
            if (string.IsNullOrWhiteSpace(st.Name)) continue;
            if (st.AnyAlive || st.Bidirectional)
                set.Add(st.Name.Trim());
        }

        if (discoveredNames != null)
        {
            foreach (var n in discoveredNames)
            {
                if (!string.IsNullOrWhiteSpace(n))
                    set.Add(n.Trim());
            }
        }

        return set.Where(n => !exclude.Contains(n)).ToList();
    }
}
