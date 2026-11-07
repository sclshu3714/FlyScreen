using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>可选作扩展屏从机的对端：双向已连接，且为电脑/大屏（或宣告 ScreenExtendSink）。</summary>
public static class ScreenExtendTargets
{
    /// <summary>会话启动用：仅双向已连接且形态合适的对端。</summary>
    public static IReadOnlyList<string> ListEligible(ConnectionManager connections)
    {
        var list = new List<string>();
        foreach (var st in connections.GetConfiguredPeerStatuses())
        {
            if (!st.Bidirectional) continue;
            if (!IsEligiblePeer(connections, st.Name)) continue;
            list.Add(st.Name);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// 下拉框用：已连接（含单向）的电脑/大屏 + 始终保留 <paramref name="alwaysInclude"/>，
    /// 避免「应用」重启网络瞬间列表被清空、已选目标丢失。
    /// </summary>
    public static IReadOnlyList<string> ListForPicker(ConnectionManager connections, string? alwaysInclude = null)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in connections.GetConfiguredPeerStatuses())
        {
            if (!st.AnyAlive) continue;
            if (!IsEligiblePeer(connections, st.Name)) continue;
            set.Add(st.Name);
        }

        if (!string.IsNullOrWhiteSpace(alwaysInclude))
            set.Add(alwaysInclude.Trim());

        return set.ToList();
    }

    public static bool IsEligiblePeer(ConnectionManager connections, string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName)) return false;
        if (connections.PeerSupports(peerName, PeerCaps.ScreenExtendSink))
            return true;

        var info = connections.GetPeerRemoteInfo(peerName);
        var dc = info?.DeviceClass;
        if (string.IsNullOrWhiteSpace(dc))
            return true; // 旧端未宣告形态：仍允许选（桌面互连场景）

        return string.Equals(dc, PeerDeviceClass.Desktop, StringComparison.OrdinalIgnoreCase)
               || string.Equals(dc, PeerDeviceClass.Tv, StringComparison.OrdinalIgnoreCase);
    }
}
