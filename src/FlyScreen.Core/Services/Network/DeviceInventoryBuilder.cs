using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 聚合局域网发现 + 布局槽位 + 已验证远程/中转设备 → 设备管理全量清单。
/// 未测试通过的连接配置不进入清单；中转只列真实扫描到的设备名。
/// </summary>
public static class DeviceInventoryBuilder
{
    public static List<DeviceInventoryItem> Build(
        IEnumerable<DiscoveredPeer> peers,
        IReadOnlyList<MachineSlot> slots,
        IReadOnlyList<ConnectionEndpoint>? endpoints,
        IReadOnlyList<RemoteBinding>? bindings,
        string localMachineName)
    {
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

        // 本地：发现 ∪ 布局（含本机）
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in peerByName.Keys)
            localNames.Add(name);
        foreach (var name in slotByName.Keys)
            localNames.Add(name);
        if (!string.IsNullOrWhiteSpace(localMachineName))
            localNames.Add(localMachineName);

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
            if (peer != null && !string.IsNullOrWhiteSpace(peer.Ip))
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
        }

        var listedRemoteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 连接配置中「已验证」的远程（一对一）
        foreach (var ep in endpoints ?? Array.Empty<ConnectionEndpoint>())
        {
            if (ep.Kind != ConnectionEndpoint.KindRemote) continue;
            if (ep.State != ConnectionEndpoint.StateConnected) continue;
            if (string.IsNullOrWhiteSpace(ep.VerifiedPeerName)) continue;

            var key = ep.VerifiedPeerName.Trim();
            if (localNames.Contains(key)) continue; // 已是局域网设备则不重复挂远程行
            if (!listedRemoteKeys.Add(key + "|" + ep.Address)) continue;

            var bound = (bindings ?? Array.Empty<RemoteBinding>()).FirstOrDefault(b =>
                string.Equals(b.EndpointId, ep.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.RemoteName, key, StringComparison.OrdinalIgnoreCase));
            var isBound = bound != null && !string.IsNullOrWhiteSpace(bound.BoundLocalDeviceId);
            var status = "在线";
            if (ep.LastRttMs is int ms) status += $" · {ms}ms";
            if (isBound)
            {
                var pos = slots.FirstOrDefault(s =>
                    string.Equals(s.Name, bound!.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel;
                status += string.IsNullOrEmpty(pos) ? $" · 已绑定：{bound!.BoundLocalDeviceId}" : $" · 已绑定：{pos}";
            }
            else
                status += " · 未绑定";

            list.Add(new DeviceInventoryItem
            {
                Id = "remote-ep:" + ep.Id,
                Name = key,
                Kind = DeviceInventoryItem.KindRemote,
                Address = string.IsNullOrWhiteSpace(ep.Address) ? "—" : ep.Address,
                StatusText = status,
                IsOnline = true,
                IsBound = isBound,
                BoundLocalDeviceId = bound?.BoundLocalDeviceId ?? "",
                BoundPositionLabel = isBound
                    ? (slots.FirstOrDefault(s =>
                        string.Equals(s.Name, bound!.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel ?? "")
                    : "",
                EndpointId = ep.Id,
                RttMs = ep.LastRttMs
            });
        }

        // 连接配置中「已验证」的中转：只列真实扫描到的设备（一对多）
        foreach (var ep in endpoints ?? Array.Empty<ConnectionEndpoint>())
        {
            if (ep.Kind != ConnectionEndpoint.KindRelay) continue;
            if (ep.State != ConnectionEndpoint.StateConnected) continue;
            if (ep.DiscoveredDevices is not { Count: > 0 }) continue;

            foreach (var d in ep.DiscoveredDevices)
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                var key = d.Name.Trim();
                if (localNames.Contains(key)) continue;
                if (!listedRemoteKeys.Add("svc:" + ep.Id + ":" + key)) continue;

                var status = d.Online ? "在线" : "离线";
                if (d.RttMs is int ms) status += $" · {ms}ms";
                status += " · 来自中转";

                list.Add(new DeviceInventoryItem
                {
                    Id = "svc-dev:" + ep.Id + ":" + (string.IsNullOrWhiteSpace(d.Id) ? key : d.Id),
                    Name = key,
                    Kind = DeviceInventoryItem.KindService,
                    Address = string.IsNullOrWhiteSpace(ep.Address) ? "—" : ep.Address,
                    StatusText = status,
                    IsOnline = d.Online,
                    EndpointId = ep.Id,
                    RttMs = d.RttMs
                });
            }
        }

        // 历史 RemoteBindings（已绑定记录），避免与上方 endpoint 行重复
        foreach (var b in bindings ?? Array.Empty<RemoteBinding>())
        {
            if (string.IsNullOrWhiteSpace(b.RemoteName) && string.IsNullOrWhiteSpace(b.Endpoint))
                continue;
            var name = string.IsNullOrWhiteSpace(b.RemoteName) ? b.Endpoint : b.RemoteName;
            if (listedRemoteKeys.Contains(name + "|" + b.Endpoint)
                || listedRemoteKeys.Contains(name)
                || list.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                                 && i.Kind != DeviceInventoryItem.KindLocal))
                continue;

            var bound = !string.IsNullOrWhiteSpace(b.BoundLocalDeviceId);
            var online = b.State is RemoteBinding.StateBound or RemoteBinding.StateProjecting;
            var status = online ? "在线" : "离线";
            if (bound)
            {
                var pos = slots.FirstOrDefault(s =>
                    string.Equals(s.Name, b.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))?.PositionLabel;
                status += string.IsNullOrEmpty(pos) ? $" · 已绑定：{b.BoundLocalDeviceId}" : $" · 已绑定：{pos}";
            }
            else
                status += " · 未绑定";

            list.Add(new DeviceInventoryItem
            {
                Id = "remote:" + b.RemoteId,
                Name = name,
                Kind = DeviceInventoryItem.KindRemote,
                Address = string.IsNullOrWhiteSpace(b.Endpoint) ? "—" : b.Endpoint,
                StatusText = status,
                IsOnline = online,
                IsBound = bound,
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
}
