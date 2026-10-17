using System.Collections.Concurrent;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 布局同步：添加 → 并集合并集群；移除 → 权威替换并踢出；最多 4 台。
/// </summary>
public sealed class LayoutSyncHelper
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private int _applyingRemoteLayout;
    private readonly ConcurrentDictionary<string, long> _lastSendToPeerTick = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? StatusMessage;
    /// <summary>本机被踢出布局后触发（UI 刷新）。</summary>
    public event Action? KickedFromLayout;

    public LayoutSyncHelper(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
        _connections.PeerLinkChanged += OnPeerLinkChanged;
        _connections.MessageReceived += OnMessageReceived;
        _connections.TryAcceptLayoutJoin = TryAcceptLayoutJoin;
    }

    public void Detach()
    {
        _connections.PeerLinkChanged -= OnPeerLinkChanged;
        _connections.MessageReceived -= OnMessageReceived;
        if (_connections.TryAcceptLayoutJoin == TryAcceptLayoutJoin)
            _connections.TryAcceptLayoutJoin = null;
    }

    /// <summary>权威替换广播（移除电脑后）。</summary>
    public Task BroadcastAsync() => BroadcastCoreAsync(LayoutSyncModes.Replace);

    public Task BroadcastReplaceAsync() => BroadcastCoreAsync(LayoutSyncModes.Replace);

    /// <summary>并集广播（添加电脑后，对端合并集群）。</summary>
    public Task BroadcastMergeAsync() => BroadcastCoreAsync(LayoutSyncModes.Merge);

    private async Task BroadcastCoreAsync(int mode)
    {
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;

        _settings.LayoutRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.EnsureLocalMachineSlot();
        _settings.Save();

        var payload = MessageCodec.JsonBytes(BuildPayload(mode));
        await _connections.BroadcastAsync(MessageType.LayoutSync, payload, PeerCaps.LayoutSync)
            .ConfigureAwait(false);
        StatusMessage?.Invoke(mode == LayoutSyncModes.Merge
            ? "布局已合并同步到其他电脑"
            : "布局已同步到其他电脑");
    }

    public async Task SendToPeerAsync(string peer)
    {
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;
        if (_settings.LayoutRevision <= 0) return;
        if (!_connections.IsPeerReachable(peer)) return;
        if (!_connections.PeerSupports(peer, PeerCaps.LayoutSync)) return;

        // 防抖：出入站各触发一次 PeerLinkChanged 时不要连发
        var now = Environment.TickCount64;
        if (_lastSendToPeerTick.TryGetValue(peer, out var last) && now - last < 800)
            return;
        _lastSendToPeerTick[peer] = now;

        // 新链路推送权威布局（Replace）：避免对端旧 Merge 把已移除机器加回来
        var payload = MessageCodec.JsonBytes(BuildPayload(LayoutSyncModes.Replace));
        await _connections.SendToAsync(peer, MessageType.LayoutSync, payload).ConfigureAwait(false);
    }

    private LayoutSyncPayload BuildPayload(int mode)
    {
        var slots = LayoutSlotMerge.Normalize(_settings.MachineSlots);
        return new LayoutSyncPayload
        {
            SourceMachine = LocalPeerIdentity.MachineName,
            MachineSlots = slots,
            TwoRow = _settings.TwoRow,
            Revision = _settings.LayoutRevision,
            Mode = mode
        };
    }

    /// <summary>对端刚把本机加入布局并请求回加。密钥已在握手里核对过。</summary>
    private bool TryAcceptLayoutJoin(string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName) || IsLocalName(peerName))
            return false;
        if (LayoutSlotMerge.ContainsName(_settings.MachineSlots, peerName))
            return true;
        if (!LayoutSlotMerge.TryMerge(_settings.MachineSlots, [peerName], out var merged))
        {
            StatusMessage?.Invoke($"无法添加 {peerName}：布局已满（最多 {LayoutSlotMerge.MaxSlots} 台）");
            return false;
        }

        _settings.MachineSlots = merged;
        _settings.LayoutRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.EnsureLocalMachineSlot();
        _settings.Save();
        StatusMessage?.Invoke($"已自动添加 {peerName}");
        try { LayoutSyncedFromPeer?.Invoke(peerName); } catch { /* ignore */ }
        return true;
    }

    private void OnMessageReceived(string peer, NetMessage msg)
    {
        if (msg.Type != MessageType.LayoutSync) return;
        HandleIncoming(peer, msg.Payload);
    }

    private void HandleIncoming(string peer, byte[] payload)
    {
        var sync = MessageCodec.DecodeJson<LayoutSyncPayload>(payload);
        if (sync == null) return;
        if (IsLocalName(sync.SourceMachine))
            return;

        var mode = sync.Mode;
        if (mode == LayoutSyncModes.MergeReject)
        {
            HandleMergeReject(peer, sync);
            return;
        }

        if (sync.MachineSlots == null || sync.MachineSlots.Count == 0)
            return;

        // 本机修订更新：忽略对端更旧的 Merge/Replace（防止移除后又被旧包加回）
        if (sync.Revision > 0 && sync.Revision < _settings.LayoutRevision)
            return;

        // 同修订且非 Merge 扩大：忽略
        if (sync.Revision > 0 && sync.Revision == _settings.LayoutRevision
            && mode != LayoutSyncModes.Merge)
            return;

        if (Interlocked.CompareExchange(ref _applyingRemoteLayout, 1, 0) != 0)
            return;

        var needRebroadcastMerge = false;
        var kicked = false;
        var slotsChanged = false;
        string? status = null;

        try
        {
            if (mode == LayoutSyncModes.Merge)
            {
                if (!LayoutSlotMerge.TryMerge(_settings.MachineSlots, sync.MachineSlots, out var merged))
                {
                    _ = SendMergeRejectAsync(peer);
                    status = $"合并布局失败（将超过 {LayoutSlotMerge.MaxSlots} 台）← {sync.SourceMachine}";
                    return;
                }

                if (LayoutSlotMerge.SameSet(merged, _settings.MachineSlots))
                {
                    // 仅抬修订号对齐，不触发 UI/重连风暴
                    if (sync.Revision > _settings.LayoutRevision)
                        _settings.LayoutRevision = sync.Revision;
                    return;
                }

                var expandedBeyondRemote = LayoutSlotMerge.CountUnique(merged)
                                           > LayoutSlotMerge.CountUnique(sync.MachineSlots);
                _settings.MachineSlots = merged;
                if (sync.TwoRow) _settings.TwoRow = true;
                _settings.LayoutRevision = Math.Max(
                    sync.Revision + 1,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _settings.EnsureLocalMachineSlot();
                _settings.Save();
                needRebroadcastMerge = expandedBeyondRemote;
                slotsChanged = true;
                status = $"已合并布局 ← {DisplayName(sync, peer)}";
            }
            else
            {
                // Replace：权威布局（同修订已在上方过滤）
                var slots = LayoutSlotMerge.Normalize(sync.MachineSlots);
                var local = LocalPeerIdentity.MachineName;
                if (!LayoutSlotMerge.ContainsName(slots, local))
                {
                    ApplyKickedSolo(local);
                    kicked = true;
                    slotsChanged = true;
                    status = $"已从布局中移除 ← {DisplayName(sync, peer)}";
                }
                else if (LayoutSlotMerge.SameSet(slots, _settings.MachineSlots)
                         && sync.TwoRow == _settings.TwoRow)
                {
                    if (sync.Revision > _settings.LayoutRevision)
                    {
                        _settings.LayoutRevision = sync.Revision;
                        _settings.Save();
                    }
                    return;
                }
                else
                {
                    _settings.MachineSlots = slots;
                    _settings.TwoRow = sync.TwoRow;
                    _settings.LayoutRevision = sync.Revision > 0
                        ? sync.Revision
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _settings.EnsureLocalMachineSlot();
                    _settings.Save();
                    slotsChanged = true;
                    status = $"已同步布局 ← {DisplayName(sync, peer)}";
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemoteLayout, 0);
        }

        if (!string.IsNullOrEmpty(status))
            StatusMessage?.Invoke(status);

        if (kicked)
        {
            try { KickedFromLayout?.Invoke(); } catch { /* ignore */ }
            try { LayoutSyncedFromPeer?.Invoke(DisplayName(sync, peer)); } catch { /* ignore */ }
            _ = DropLinksQuietAsync();
            return;
        }

        if (slotsChanged)
        {
            try { LayoutSyncedFromPeer?.Invoke(DisplayName(sync!, peer)); } catch { /* ignore */ }
            // 去掉已不在布局中的链路；新成员由 MaintainLoop 自动出站，无需整网重启
            _ = _connections.PruneUnconfiguredLinksAsync();
        }

        if (needRebroadcastMerge)
            _ = BroadcastMergeAsync();
    }

    private void HandleMergeReject(string peer, LayoutSyncPayload sync)
    {
        var rejector = string.IsNullOrWhiteSpace(sync.SourceMachine) ? peer : sync.SourceMachine;
        if (IsLocalName(rejector)) return;
        if (!LayoutSlotMerge.ContainsName(_settings.MachineSlots, rejector)) return;
        if (Interlocked.CompareExchange(ref _applyingRemoteLayout, 1, 0) != 0) return;

        try
        {
            var kept = LayoutSlotMerge.NonEmptyNames(_settings.MachineSlots)
                .Where(n => !string.Equals(n, rejector, StringComparison.OrdinalIgnoreCase))
                .ToList();
            while (kept.Count < LayoutSlotMerge.MaxSlots) kept.Add("");
            _settings.MachineSlots = kept;
            _settings.LayoutRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settings.EnsureLocalMachineSlot();
            _settings.Save();
            StatusMessage?.Invoke($"添加 {rejector} 失败：合并后将超过 {LayoutSlotMerge.MaxSlots} 台，已回滚");
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemoteLayout, 0);
        }

        try { LayoutSyncedFromPeer?.Invoke(rejector); } catch { /* ignore */ }
        _ = _connections.DropPeerAsync(rejector);
        _ = BroadcastReplaceAsync();
    }

    private async Task SendMergeRejectAsync(string peer)
    {
        try
        {
            if (!_connections.IsPeerReachable(peer)) return;
            var payload = MessageCodec.JsonBytes(new LayoutSyncPayload
            {
                SourceMachine = LocalPeerIdentity.MachineName,
                MachineSlots = LayoutSlotMerge.Normalize(_settings.MachineSlots),
                TwoRow = _settings.TwoRow,
                Revision = _settings.LayoutRevision,
                Mode = LayoutSyncModes.MergeReject
            });
            await _connections.SendToAsync(peer, MessageType.LayoutSync, payload).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyKickedSolo(string local)
    {
        _settings.MachineSlots = [local, "", "", ""];
        _settings.LayoutRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.Save();
    }

    private async Task DropLinksQuietAsync()
    {
        try
        {
            await _connections.DropAllPeerLinksAsync().ConfigureAwait(false);
            StatusMessage?.Invoke("已断开与布局中其他电脑的连接");
        }
        catch
        {
            // ignore
        }
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        if (!(outbound || inbound)) return;
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;
        // 仅当对端仍在本机布局中才推送，避免已移除机器的幽灵链路触发同步
        if (!LayoutSlotMerge.ContainsName(_settings.MachineSlots, name)) return;
        _ = SendToPeerAsync(name);
    }

    private static string DisplayName(LayoutSyncPayload sync, string peer)
        => string.IsNullOrWhiteSpace(sync.SourceMachine) ? peer : sync.SourceMachine;

    private static bool IsLocalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }
}
