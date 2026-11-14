using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Clipboard;

/// <summary>
/// 直传「常用位置」列表：按<strong>目标端</strong>形态过滤。
/// 传往手机/平板时只给玄鸟收件箱 + 自定义（默认 Inbox）；传往电脑仍为五常用位置。
/// </summary>
public static class DirectTransferDestinations
{
    public static bool IsMobileReceiveTarget(PeerRemoteInfo? info)
        => PeerComposition.NeedsHostComposition(info);

    /// <summary>返回 (显示名, 绝对路径)；自定义项始终在末尾。</summary>
    public static List<(string Label, string Path)> BuildPresets(
        RemoteFoldersPayload? folders,
        PeerRemoteInfo? peerInfo)
    {
        var list = new List<(string Label, string Path)>();
        if (folders == null)
        {
            list.Add((Loc.T("DT.CustomPath"), ""));
            return list;
        }

        var inbox = (folders.Inbox ?? "").Trim();
        if (IsMobileReceiveTarget(peerInfo))
        {
            if (!string.IsNullOrEmpty(inbox))
                list.Add((Loc.T("DT.Inbox"), inbox));
            list.Add((Loc.T("DT.CustomPath"), inbox));
            return list;
        }

        void Add(string label, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                list.Add((label, path.Trim()));
        }

        Add(Loc.T("DT.Desktop"), folders.Desktop);
        Add(Loc.T("DT.Documents"), folders.Documents);
        Add(Loc.T("DT.Downloads"), folders.Downloads);
        Add(Loc.T("DT.Inbox"), inbox);

        var customDefault = !string.IsNullOrWhiteSpace(folders.Downloads)
            ? folders.Downloads.Trim()
            : inbox;
        list.Add((Loc.T("DT.CustomPath"), customDefault));
        return list;
    }
}
