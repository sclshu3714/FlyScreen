using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 远程/中转设备 ↔ 电脑布局槽位绑定。本机 settings + RemoteBindingSync 跨布局电脑同步。
/// </summary>
public static class RemoteBindingHelper
{
    public readonly record struct LayoutTarget(string DeviceId, string Label, string PositionLabel)
    {
        public override string ToString() => Label;
    }

    public static IReadOnlyList<LayoutTarget> ListLayoutTargets(IEnumerable<MachineSlot> slots)
    {
        var list = new List<LayoutTarget>();
        foreach (var s in slots)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            var id = s.Name.Trim();
            list.Add(new LayoutTarget(id, $"{s.PositionLabel} — {id}", s.PositionLabel));
        }

        return list;
    }

    public static IReadOnlyList<RemoteBinding> ListPool(AppSettings settings, string? localDeviceId)
    {
        if (string.IsNullOrWhiteSpace(localDeviceId)) return [];
        settings.EnsureCollections();
        return settings.RemoteBindings
            .Where(b => string.Equals(b.BoundLocalDeviceId, localDeviceId.Trim(), StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(b.RemoteName))
            .OrderBy(b => b.RemoteName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static RemoteBinding? GetActive(AppSettings settings, string? localDeviceId)
    {
        var pool = ListPool(settings, localDeviceId);
        if (pool.Count == 0) return null;
        settings.EnsureCollections();
        var key = localDeviceId!.Trim();
        if (settings.ActiveRemoteByLocalDevice.TryGetValue(key, out var id)
            && pool.FirstOrDefault(b => string.Equals(b.RemoteId, id, StringComparison.OrdinalIgnoreCase)) is { } hit)
            return hit;
        return pool[0];
    }

    public static void EnsureActive(AppSettings settings, string localDeviceId)
    {
        settings.EnsureCollections();
        var key = localDeviceId.Trim();
        var pool = ListPool(settings, key);
        if (pool.Count == 0)
        {
            settings.ActiveRemoteByLocalDevice.Remove(key);
            return;
        }

        if (!settings.ActiveRemoteByLocalDevice.TryGetValue(key, out var id)
            || pool.All(b => !string.Equals(b.RemoteId, id, StringComparison.OrdinalIgnoreCase)))
        {
            settings.ActiveRemoteByLocalDevice[key] = pool[0].RemoteId;
        }
    }

    public static bool SetActive(AppSettings settings, string localDeviceId, string remoteId)
    {
        settings.EnsureCollections();
        var pool = ListPool(settings, localDeviceId);
        var hit = pool.FirstOrDefault(b => string.Equals(b.RemoteId, remoteId, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return false;
        if (pool.Any(b => b.State == RemoteBinding.StateProjecting))
            return false; // 投影中禁止切换
        settings.ActiveRemoteByLocalDevice[localDeviceId.Trim()] = hit.RemoteId;
        return true;
    }

    public static RemoteBinding? GetProjecting(AppSettings settings, string? localDeviceId)
    {
        var active = GetActive(settings, localDeviceId);
        return active is { State: RemoteBinding.StateProjecting } ? active : null;
    }

    /// <summary>穿边用：投影中返回远程名，否则返回本地槽位名。</summary>
    public static string? ResolveEdgePeerName(AppSettings settings, string? localSlotName)
    {
        if (string.IsNullOrWhiteSpace(localSlotName)) return localSlotName;
        var projecting = GetProjecting(settings, localSlotName);
        return projecting != null ? projecting.RemoteName.Trim() : localSlotName.Trim();
    }

    public static void SetProjecting(AppSettings settings, RemoteBinding binding, bool projecting)
    {
        settings.EnsureCollections();
        var local = binding.BoundLocalDeviceId?.Trim() ?? "";
        foreach (var b in settings.RemoteBindings)
        {
            if (!string.Equals(b.BoundLocalDeviceId, local, StringComparison.OrdinalIgnoreCase))
                continue;
            if (projecting && string.Equals(b.RemoteId, binding.RemoteId, StringComparison.OrdinalIgnoreCase))
                b.State = RemoteBinding.StateProjecting;
            else if (b.State == RemoteBinding.StateProjecting)
                b.State = RemoteBinding.StateBound;
        }

        if (projecting)
            settings.ActiveRemoteByLocalDevice[local] = binding.RemoteId;
    }

    /// <summary>
    /// 投影会话结束后清除「投影中」标记，避免穿边目标残留为远程名。
    /// 仅清 <paramref name="localDeviceId"/> 绑定的项；勿清同步来的其它电脑投影态。
    /// </summary>
    public static bool ClearProjectingForLocal(AppSettings settings, string? localDeviceId)
    {
        if (string.IsNullOrWhiteSpace(localDeviceId)) return false;
        settings.EnsureCollections();
        var key = localDeviceId.Trim();
        var changed = false;
        foreach (var b in settings.RemoteBindings)
        {
            if (b.State != RemoteBinding.StateProjecting) continue;
            if (!string.Equals(b.BoundLocalDeviceId, key, StringComparison.OrdinalIgnoreCase))
                continue;
            b.State = RemoteBinding.StateBound;
            changed = true;
        }

        return changed;
    }

    /// <summary>清除本机全部槽位上的「投影中」（本机多绑定场景）。</summary>
    public static bool ClearAllProjecting(AppSettings settings)
        => ClearProjectingForLocal(settings, LocalPeerIdentity.MachineName);

    public static bool MatchesInventoryItem(RemoteBinding b, DeviceInventoryItem item)
    {
        if (string.Equals("remote:" + b.RemoteId, item.Id, StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.Kind == DeviceInventoryItem.KindService)
        {
            if (!string.IsNullOrWhiteSpace(item.Name)
                && string.Equals(b.RemoteName, item.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(item.EndpointId) || string.IsNullOrWhiteSpace(b.EndpointId))
                    return true;
                return string.Equals(b.EndpointId, item.EndpointId, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(item.EndpointId)
                   && string.Equals(b.EndpointId, item.EndpointId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(b.RemoteName, item.Name, StringComparison.OrdinalIgnoreCase);
        }

        if (item.Kind == DeviceInventoryItem.KindRemote)
        {
            if (!string.IsNullOrWhiteSpace(item.EndpointId)
                && string.Equals(b.EndpointId, item.EndpointId, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(item.Name)
                && string.Equals(b.RemoteName, item.Name, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(item.EndpointId)
                    || string.Equals(b.EndpointId, item.EndpointId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(b.EndpointId)))
                return true;
        }

        return false;
    }

    public static RemoteBinding? Find(IEnumerable<RemoteBinding>? bindings, DeviceInventoryItem item)
        => (bindings ?? Array.Empty<RemoteBinding>()).FirstOrDefault(b => MatchesInventoryItem(b, item));

    public static RemoteBinding? FindById(AppSettings settings, string? remoteId)
    {
        if (string.IsNullOrWhiteSpace(remoteId)) return null;
        settings.EnsureCollections();
        return settings.RemoteBindings.FirstOrDefault(b =>
            string.Equals(b.RemoteId, remoteId, StringComparison.OrdinalIgnoreCase));
    }

    public static RemoteBinding Bind(AppSettings settings, DeviceInventoryItem item, string localDeviceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(localDeviceId))
            throw new ArgumentException("localDeviceId required", nameof(localDeviceId));

        settings.EnsureCollections();
        var localId = localDeviceId.Trim();
        var endpoint = NormalizeEndpoint(item.Address);
        var mode = item.Kind == DeviceInventoryItem.KindService
            ? RemoteBinding.ModeRelay
            : RemoteBinding.ModeP2P;

        string? oldLocal = null;
        var existing = Find(settings.RemoteBindings, item);
        if (existing != null)
        {
            oldLocal = existing.BoundLocalDeviceId;
            if (existing.State == RemoteBinding.StateProjecting)
                existing.State = RemoteBinding.StateBound;
            existing.BoundLocalDeviceId = localId;
            existing.RemoteName = string.IsNullOrWhiteSpace(item.Name) ? existing.RemoteName : item.Name.Trim();
            if (!string.IsNullOrWhiteSpace(item.EndpointId))
                existing.EndpointId = item.EndpointId;
            if (!string.IsNullOrWhiteSpace(endpoint))
                existing.Endpoint = endpoint;
            existing.Mode = mode;
            if (item.RttMs is int ms)
                existing.RttMs = ms;

            if (!string.IsNullOrWhiteSpace(oldLocal)
                && !string.Equals(oldLocal, localId, StringComparison.OrdinalIgnoreCase))
                EnsureActive(settings, oldLocal);
            EnsureActive(settings, localId);
            // 改绑到新电脑：若该池尚无当前，用这台
            var pool = ListPool(settings, localId);
            if (pool.Count == 1)
                settings.ActiveRemoteByLocalDevice[localId] = existing.RemoteId;
            return existing;
        }

        var created = new RemoteBinding
        {
            RemoteName = item.Name?.Trim() ?? "",
            BoundLocalDeviceId = localId,
            EndpointId = item.EndpointId ?? "",
            Endpoint = endpoint,
            Mode = mode,
            State = RemoteBinding.StateBound,
            RttMs = item.RttMs
        };
        settings.RemoteBindings.Add(created);
        EnsureActive(settings, localId);
        if (ListPool(settings, localId).Count == 1)
            settings.ActiveRemoteByLocalDevice[localId] = created.RemoteId;
        return created;
    }

    /// <summary>改绑到另一台本地电脑（更换）。</summary>
    public static RemoteBinding Rebind(AppSettings settings, RemoteBinding binding, string newLocalDeviceId)
    {
        settings.EnsureCollections();
        var oldLocal = binding.BoundLocalDeviceId;
        if (binding.State == RemoteBinding.StateProjecting)
            binding.State = RemoteBinding.StateBound;
        binding.BoundLocalDeviceId = newLocalDeviceId.Trim();
        if (!string.IsNullOrWhiteSpace(oldLocal))
            EnsureActive(settings, oldLocal);
        EnsureActive(settings, binding.BoundLocalDeviceId);
        if (ListPool(settings, binding.BoundLocalDeviceId).Count == 1)
            settings.ActiveRemoteByLocalDevice[binding.BoundLocalDeviceId] = binding.RemoteId;
        return binding;
    }

    public static int Unbind(AppSettings settings, DeviceInventoryItem item)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(item);
        settings.EnsureCollections();
        var hit = Find(settings.RemoteBindings, item);
        var local = hit?.BoundLocalDeviceId;
        var n = settings.RemoteBindings.RemoveAll(b => MatchesInventoryItem(b, item));
        if (!string.IsNullOrWhiteSpace(local))
            EnsureActive(settings, local);
        return n;
    }

    public static int UnbindBinding(AppSettings settings, RemoteBinding binding)
    {
        settings.EnsureCollections();
        var local = binding.BoundLocalDeviceId;
        var n = settings.RemoteBindings.RemoveAll(b =>
            string.Equals(b.RemoteId, binding.RemoteId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(local))
            EnsureActive(settings, local);
        return n;
    }

    /// <summary>把远程池状态刷到布局槽位（影子子卡）。</summary>
    public static void ApplyToSlots(IEnumerable<MachineSlot> slots, AppSettings settings)
    {
        settings.EnsureCollections();
        foreach (var slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Name))
            {
                slot.ClearRemoteCard();
                continue;
            }

            var pool = ListPool(settings, slot.Name);
            var active = GetActive(settings, slot.Name);
            slot.BoundRemoteCount = pool.Count;
            slot.CanSwitchRemote = pool.Count > 1;
            if (active == null)
            {
                slot.ClearRemoteCard();
                slot.BoundRemoteCount = pool.Count;
                slot.CanSwitchRemote = pool.Count > 1;
                continue;
            }

            var mode = active.Mode == RemoteBinding.ModeP2P ? "P2P" : "中转";
            var projecting = active.State == RemoteBinding.StateProjecting;
            var online = active.State is RemoteBinding.StateBound or RemoteBinding.StateProjecting;
            slot.ActiveRemoteId = active.RemoteId;
            slot.ActiveRemoteName = active.RemoteName.Trim();
            slot.ActiveRemoteModeLabel = mode;
            slot.ActiveRemoteIsProjecting = projecting;
            slot.ActiveRemoteStatusText = projecting
                ? $"● 投影中 · [{mode}]"
                : online
                    ? (active.RttMs is int ms ? $"● 已连接 · [{mode}] {ms}ms" : $"● 已连接 · [{mode}]")
                    : $"○ 离线 · [{mode}]";
            slot.ActiveRemoteBindHint = $"绑定：{slot.PositionLabel} / {slot.Name}";
            slot.HasActiveRemote = true;
            slot.ShowAsRemotePrimary = projecting;
            slot.BoundRemotesText = pool.Count <= 1
                ? $"远程 · {active.RemoteName}"
                : $"远程 · {active.RemoteName}（{pool.Count}）";
        }
    }

    // 兼容旧调用
    public static void ApplyToSlots(IEnumerable<MachineSlot> slots, IEnumerable<RemoteBinding>? bindings)
    {
        var list = slots.ToList();
        // 无 AppSettings 时仅填摘要
        foreach (var slot in list)
        {
            if (string.IsNullOrWhiteSpace(slot.Name))
            {
                slot.ClearRemoteCard();
                continue;
            }

            var names = (bindings ?? Array.Empty<RemoteBinding>())
                .Where(b => string.Equals(b.BoundLocalDeviceId, slot.Name, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.RemoteName.Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            slot.BoundRemotesText = names.Count == 0 ? "" : "远程 · " + string.Join("、", names);
            slot.BoundRemoteCount = names.Count;
            slot.HasActiveRemote = names.Count > 0;
            if (names.Count > 0)
            {
                slot.ActiveRemoteName = names[0];
                slot.ActiveRemoteStatusText = "● 已连接";
                slot.ActiveRemoteBindHint = $"绑定：{slot.PositionLabel} / {slot.Name}";
                slot.CanSwitchRemote = names.Count > 1;
            }
            else
                slot.ClearRemoteCard();
        }
    }

    private static string NormalizeEndpoint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        var a = address.Trim();
        return a is "—" or "-" ? "" : a;
    }
}
