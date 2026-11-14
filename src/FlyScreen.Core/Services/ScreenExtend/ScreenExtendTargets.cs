using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>
/// 主窗体「投影」目标（与设备管理远程/中转投影完全分离）。
/// 来源以「电脑布局」槽位为准：布局内电脑↔电脑、布局内手机→电脑均可；手机不作接收端。
/// </summary>
public static class ScreenExtendTargets
{
    /// <summary>可开始投影的布局内已连通对端。</summary>
    public static IReadOnlyList<string> ListEligible(ConnectionManager connections)
    {
        var list = new List<string>();
        foreach (var name in ListForPicker(connections))
        {
            if (!connections.GetPeerStatus(name).Bidirectional) continue;
            list.Add(name);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// 主窗投影下拉：电脑布局槽位中的合格电脑（已在布局内的对端不再用发现来源二次否决，
    /// 避免局域网电脑被远程探测标成 Remote 后从列表消失）。
    /// 手机槽位可能尚未写入对端电脑名时，额外并入已连通且非远程的局域网电脑。
    /// </summary>
    public static IReadOnlyList<string> ListForPicker(ConnectionManager connections, string? preferPeer = null)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in connections.LayoutPeers())
        {
            if (!IsEligiblePeer(connections, name)) continue;
            set.Add(name);
        }

        // 手机/平板：对端电脑可能只在链路上（本机槽位未写入），补已连通的局域网电脑
        if (IsMobileClient())
        {
            foreach (var name in connections.ListLivePeerNames())
            {
                if (set.Contains(name)) continue;
                // 补列表时仍排除远程/中转（那些走设备管理投影）
                if (connections.IsLayoutIneligiblePeer(name)) continue;
                if (!IsEligiblePeer(connections, name)) continue;
                set.Add(name);
            }
        }

        // prefer 只影响调用方选中，绝不把布局外远程塞进列表
        _ = preferPeer;

        return set.ToList();
    }

    /// <summary>
    /// 主窗投影接收端形态：电脑/大屏，或对端宣告 ScreenExtendSink。
    /// 不在此用发现来源否决——布局成员资格由 <see cref="ListForPicker"/> / 入布局路径单独处理。
    /// </summary>
    public static bool IsEligiblePeer(ConnectionManager connections, string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName)) return false;

        var info = connections.GetPeerRemoteInfo(peerName);
        var dc = info?.DeviceClass;

        if (string.Equals(dc, PeerDeviceClass.Phone, StringComparison.OrdinalIgnoreCase)
            || string.Equals(dc, PeerDeviceClass.Tablet, StringComparison.OrdinalIgnoreCase))
            return false;

        // 对端自身 Caps（勿用 PeerSupports/Intersect：手机无 Sink 会把电脑 Sink 交掉）
        if (connections.PeerAdvertises(peerName, PeerCaps.ScreenExtendSink))
            return true;

        if (string.IsNullOrWhiteSpace(dc)
            || string.Equals(dc, PeerDeviceClass.Unknown, StringComparison.OrdinalIgnoreCase))
            return true; // 尚未握完手或旧端：仍允许列出

        return string.Equals(dc, PeerDeviceClass.Desktop, StringComparison.OrdinalIgnoreCase)
               || string.Equals(dc, PeerDeviceClass.Tv, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMobileClient()
        => string.Equals(LocalPeerIdentity.DeviceClass, PeerDeviceClass.Phone, StringComparison.OrdinalIgnoreCase)
           || string.Equals(LocalPeerIdentity.DeviceClass, PeerDeviceClass.Tablet, StringComparison.OrdinalIgnoreCase);
}
