using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 聚合发现表 + 布局槽位 + 中转扫描/历史绑定 → 设备管理全量清单。
/// 类型：局域网发现=本地；连接配置远程探测=远程；中转服务宣告=服务。
/// </summary>
public static class DeviceInventoryBuilder
{
    public static List<DeviceInventoryItem> Build(
        IEnumerable<DiscoveredPeer> peers,
        IReadOnlyList<MachineSlot> slots,
        IReadOnlyList<ConnectionEndpoint>? endpoints,
        IReadOnlyList<RemoteBinding>? bindings,
        string localMachineName,
        string? projectingPeerName = null,
        string? localDisplayIp = null)
    {
        var projecting = (projectingPeerName ?? "").Trim();
        var localIp = (localDisplayIp ?? "").Trim();
        var list = new List<DeviceInventoryItem>();
        var peerByName = peers
            .Where(p => !string.IsNullOrWhiteSpace(p.MachineName))
            .GroupBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LastSeenUtc).First(), StringComparer.OrdinalIgnoreCase);

        var slotByName = new Dictionary<string, (MachineSlot Slot, int Index)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            slotByName[s.Name.Trim()] = (s, i);
        }

        var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(localMachineName))
            listedNames.Add(localMachineName);

        // —— 本机 + 局域网发现 + 仅在布局中的槽位 → 本地 ——
        // 匹配「连接配置·远程」的对端即使 Origin 曾被误标 Lan，也不进本地行
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(localMachineName))
            localNames.Add(localMachineName);
        foreach (var (name, peer) in peerByName)
        {
            if (string.Equals(name, localMachineName, StringComparison.OrdinalIgnoreCase))
            {
                localNames.Add(name);
                continue;
            }

            if (MatchesRemoteEndpoint(name, peer, endpoints)
                || peer.Origin is DiscoveryOrigin.Remote or DiscoveryOrigin.Service
                || IsCrossNetworkPeer(peer))
                continue;

            if (peer.Origin == DiscoveryOrigin.Lan)
                localNames.Add(name);
        }
        foreach (var name in slotByName.Keys)
        {
            // 布局里的机器若已被标为远程/服务，或命中远程连接配置，不再当本地行
            if (string.Equals(name, localMachineName, StringComparison.OrdinalIgnoreCase))
            {
                localNames.Add(name);
                continue;
            }

            if (peerByName.TryGetValue(name, out var p))
            {
                if (p.Origin is DiscoveryOrigin.Remote or DiscoveryOrigin.Service
                    || MatchesRemoteEndpoint(name, p, endpoints)
                    || IsCrossNetworkPeer(p))
                    continue;
            }

            localNames.Add(name);
        }

        foreach (var name in localNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            peerByName.TryGetValue(name, out var peer);
            var hasSlot = slotByName.TryGetValue(name, out var slotInfo);
            var slot = hasSlot ? slotInfo.Slot : null;
            var isLocal = string.Equals(name, localMachineName, StringComparison.OrdinalIgnoreCase);
            var inLayout = slot != null;
            var online = peer != null || (inLayout && (slot!.OutboundOk || slot.InboundOk || isLocal));
            if (isLocal) online = true;

            var address = "—";
            if (isLocal && !string.IsNullOrWhiteSpace(localIp) && localIp != "0.0.0.0")
            {
                var port = peer is { TcpPort: > 0 } ? peer.TcpPort : 15155;
                address = $"{localIp}:{port}";
            }
            else if (peer != null && !string.IsNullOrWhiteSpace(peer.Ip))
                address = peer.TcpPort > 0 ? $"{peer.Ip}:{peer.TcpPort}" : peer.Ip;
            else if (inLayout && !string.IsNullOrWhiteSpace(slot!.ResolvedIp))
                address = slot.ResolvedIp;

            var status = online ? "在线" : "离线";
            if (inLayout)
                status += $" · 已在布局：{slot!.PositionLabel}";
            else if (isLocal)
                status += " · 本机";
            else
                status += " · 未加入布局";

            list.Add(new DeviceInventoryItem
            {
                Id = "local:" + name,
                Name = isLocal ? $"{name}（本机）" : name,
                Kind = DeviceInventoryItem.KindLocal,
                Address = address,
                StatusText = status,
                IsOnline = online,
                IsLocalMachine = isLocal,
                LayoutSlotIndex = inLayout ? slotInfo.Index : -1,
                LayoutPositionLabel = inLayout ? slot!.PositionLabel : "",
                Peer = peer
            });
            listedNames.Add(name);
        }

        // —— 远程配置探测到的对端（含 Origin 误标 Lan 但地址命中远程连接配置）——
        foreach (var (name, peer) in peerByName.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (listedNames.Contains(name)) continue;
            var isRemote = peer.Origin == DiscoveryOrigin.Remote
                           || MatchesRemoteEndpoint(name, peer, endpoints)
                           || IsCrossNetworkPeer(peer);
            if (!isRemote) continue;
            // Origin=Service 走中转段；此处只收远程
            if (peer.Origin == DiscoveryOrigin.Service
                && !MatchesRemoteEndpoint(name, peer, endpoints)
                && !IsCrossNetworkPeer(peer))
                continue;

            var item = BuildNonLocalFromPeer(
                peer, DeviceInventoryItem.KindRemote, "来自远程配置", slots, bindings, endpoints, projecting);
            list.Add(item);
            listedNames.Add(name);
        }

        // —— 中转服务 UDP/扫描到的设备 ——
        foreach (var (name, peer) in peerByName.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (peer.Origin != DiscoveryOrigin.Service) continue;
            if (listedNames.Contains(name)) continue;

            var item = BuildNonLocalFromPeer(
                peer, DeviceInventoryItem.KindService, "来自中转服务", slots, bindings, endpoints, projecting);
            list.Add(item);
            listedNames.Add(name);
        }

        var listedRemoteKeys = new HashSet<string>(listedNames, StringComparer.OrdinalIgnoreCase);

        // 中转 endpoint.DiscoveredDevices（HTTP/WS 扫描路径；与 UDP Origin=service 互补）
        foreach (var ep in endpoints ?? Array.Empty<ConnectionEndpoint>())
        {
            if (ep.Kind != ConnectionEndpoint.KindRelay) continue;
            if (ep.State != ConnectionEndpoint.StateConnected) continue;
            if (ep.DiscoveredDevices is not { Count: > 0 }) continue;

            foreach (var d in ep.DiscoveredDevices)
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                var key = d.Name.Trim();
                if (listedNames.Contains(key)) continue;
                if (!listedRemoteKeys.Add("svc:" + ep.Id + ":" + key)) continue;

                var bound = (bindings ?? Array.Empty<RemoteBinding>()).FirstOrDefault(b =>
                    string.Equals(b.EndpointId, ep.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.RemoteName, key, StringComparison.OrdinalIgnoreCase));
                var isBound = bound != null && !string.IsNullOrWhiteSpace(bound.BoundLocalDeviceId);
                var isProjecting = !string.IsNullOrEmpty(projecting)
                    && string.Equals(key, projecting, StringComparison.OrdinalIgnoreCase);

                var status = d.Online ? "在线" : "离线";
                if (d.RttMs is int ms) status += $" · {ms}ms";
                status += " · 来自中转";
                status += FormatProjectStatus(isProjecting, isBound, bound, slots);

                list.Add(new DeviceInventoryItem
                {
                    Id = "svc-dev:" + ep.Id + ":" + (string.IsNullOrWhiteSpace(d.Id) ? key : d.Id),
                    Name = key,
                    Kind = DeviceInventoryItem.KindService,
                    Address = string.IsNullOrWhiteSpace(ep.Address) ? "—" : ep.Address,
                    StatusText = status,
                    IsOnline = d.Online,
                    IsBound = isBound,
                    IsProjecting = isProjecting,
                    BoundLocalDeviceId = bound?.BoundLocalDeviceId ?? "",
                    BoundPositionLabel = isBound
                        ? (slots.FirstOrDefault(s =>
                            string.Equals(s.Name, bound!.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel ?? "")
                        : "",
                    EndpointId = ep.Id,
                    RttMs = d.RttMs
                });
                listedNames.Add(key);
            }
        }

        // 历史影子绑定
        foreach (var b in bindings ?? Array.Empty<RemoteBinding>())
        {
            if (string.IsNullOrWhiteSpace(b.RemoteName) && string.IsNullOrWhiteSpace(b.Endpoint))
                continue;
            var name = string.IsNullOrWhiteSpace(b.RemoteName) ? b.Endpoint : b.RemoteName;
            if (listedNames.Contains(name)) continue;
            if (list.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                              && i.Kind != DeviceInventoryItem.KindLocal))
                continue;

            var bound = !string.IsNullOrWhiteSpace(b.BoundLocalDeviceId);
            var online = b.State is RemoteBinding.StateBound or RemoteBinding.StateProjecting;
            var isProjecting = !string.IsNullOrEmpty(projecting)
                && string.Equals(name, projecting, StringComparison.OrdinalIgnoreCase);
            var status = online ? "在线" : "离线";
            status += FormatProjectStatus(isProjecting, bound, bound ? b : null, slots);

            list.Add(new DeviceInventoryItem
            {
                Id = "remote:" + b.RemoteId,
                Name = name,
                Kind = b.Mode == RemoteBinding.ModeRelay
                    ? DeviceInventoryItem.KindService
                    : DeviceInventoryItem.KindRemote,
                Address = string.IsNullOrWhiteSpace(b.Endpoint) ? "—" : b.Endpoint,
                StatusText = status,
                IsOnline = online,
                IsBound = bound,
                IsProjecting = isProjecting,
                BoundLocalDeviceId = b.BoundLocalDeviceId,
                BoundPositionLabel = bound
                    ? (slots.FirstOrDefault(s =>
                        string.Equals(s.Name, b.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel ?? "")
                    : "",
                EndpointId = b.EndpointId,
                RttMs = b.RttMs
            });
        }

        return list;
    }

    private static string FormatProjectStatus(
        bool isProjecting,
        bool isBound,
        RemoteBinding? bound,
        IReadOnlyList<MachineSlot> slots)
    {
        if (isProjecting)
            return " · 投影中";
        if (isBound && bound != null)
        {
            var pos = slots.FirstOrDefault(s =>
                string.Equals(s.Name, bound.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel;
            return string.IsNullOrEmpty(pos)
                ? $" · 已投影到：{bound.BoundLocalDeviceId}"
                : $" · 已投影到：{pos}";
        }

        return " · 未投影";
    }

    private static DeviceInventoryItem BuildNonLocalFromPeer(
        DiscoveredPeer peer,
        int kind,
        string sourceHint,
        IReadOnlyList<MachineSlot> slots,
        IReadOnlyList<RemoteBinding>? bindings,
        IReadOnlyList<ConnectionEndpoint>? endpoints,
        string projectingPeerName)
    {
        var name = peer.MachineName.Trim();
        var address = peer.TcpPort > 0 ? $"{peer.Ip}:{peer.TcpPort}" : peer.Ip;
        if (string.IsNullOrWhiteSpace(address)) address = "—";

        var epId = FindEndpointId(name, kind, endpoints, peer);
        var bound = (bindings ?? Array.Empty<RemoteBinding>()).FirstOrDefault(b =>
            string.Equals(b.RemoteName, name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(epId)
                || string.IsNullOrWhiteSpace(b.EndpointId)
                || string.Equals(b.EndpointId, epId, StringComparison.OrdinalIgnoreCase)));
        var isBound = bound != null && !string.IsNullOrWhiteSpace(bound.BoundLocalDeviceId);
        var isProjecting = !string.IsNullOrEmpty(projectingPeerName)
            && string.Equals(name, projectingPeerName, StringComparison.OrdinalIgnoreCase);

        var status = "在线";
        status += $" · {sourceHint}";
        status += FormatProjectStatus(isProjecting, isBound, bound, slots);

        return new DeviceInventoryItem
        {
            Id = (kind == DeviceInventoryItem.KindService ? "svc-peer:" : "remote-peer:") + name,
            Name = name,
            Kind = kind,
            Address = address,
            StatusText = status,
            IsOnline = true,
            IsBound = isBound,
            IsProjecting = isProjecting,
            BoundLocalDeviceId = bound?.BoundLocalDeviceId ?? "",
            BoundPositionLabel = isBound
                ? (slots.FirstOrDefault(s =>
                    string.Equals(s.Name, bound!.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel ?? "")
                : "",
            EndpointId = epId,
            Peer = peer
        };
    }

    private static string FindEndpointId(
        string name,
        int kind,
        IReadOnlyList<ConnectionEndpoint>? endpoints,
        DiscoveredPeer peer)
    {
        foreach (var ep in endpoints ?? Array.Empty<ConnectionEndpoint>())
        {
            var epIsRelay = ep.Kind == ConnectionEndpoint.KindRelay;
            var epIsRemote = ep.Kind == ConnectionEndpoint.KindRemote;
            if (kind == DeviceInventoryItem.KindRemote && !epIsRemote) continue;
            // 服务：中转行，或把中继 IP 填在「远程」行时也认
            if (kind == DeviceInventoryItem.KindService && !epIsRelay && !epIsRemote) continue;

            if (ep.DiscoveredDevices?.Any(d =>
                    string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
                return ep.Id;

            if (!string.IsNullOrWhiteSpace(ep.VerifiedPeerName)
                && string.Equals(ep.VerifiedPeerName, name, StringComparison.OrdinalIgnoreCase))
                return ep.Id;

            if (AddressMentionsHost(ep.Address, peer.ObservedIp)
                || AddressMentionsHost(ep.Address, peer.Ip))
                return ep.Id;
        }

        return "";
    }

    /// <summary>是否命中「连接配置」里的远程行（按验证名或地址）。</summary>
    private static bool MatchesRemoteEndpoint(
        string name,
        DiscoveredPeer peer,
        IReadOnlyList<ConnectionEndpoint>? endpoints)
    {
        foreach (var ep in endpoints ?? Array.Empty<ConnectionEndpoint>())
        {
            if (ep.Kind != ConnectionEndpoint.KindRemote)
                continue;
            if (!string.IsNullOrWhiteSpace(ep.VerifiedPeerName)
                && string.Equals(ep.VerifiedPeerName, name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (AddressMentionsHost(ep.Address, peer.Ip)
                || AddressMentionsHost(ep.Address, peer.ObservedIp))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Tailscale CGNAT 或对端 IP 不在本机物理网卡同网段 → 远程
    /// （经 overlay/跨网互通时，双方设备管理应互标远程）。
    /// </summary>
    private static bool IsCrossNetworkPeer(DiscoveredPeer peer)
    {
        if (NicService.IsTailscaleCgNat(peer.Ip) || NicService.IsTailscaleCgNat(peer.ObservedIp))
            return true;
        var hasIp = !string.IsNullOrWhiteSpace(peer.Ip) || !string.IsNullOrWhiteSpace(peer.ObservedIp);
        if (!hasIp) return false;
        return !NicService.IsOnLocalPhysicalSubnet(peer.Ip)
               && !NicService.IsOnLocalPhysicalSubnet(peer.ObservedIp);
    }

    private static bool AddressMentionsHost(string? address, string? host)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(host)) return false;
        return address.Contains(host, StringComparison.OrdinalIgnoreCase);
    }
}
