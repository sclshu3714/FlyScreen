using System.Net.Sockets;
using FlyScreen.Models;

namespace FlyScreen.Services.Network.Relay;

/// <summary>按连接配置 Endpoint.Id 管理多路中转 WS 会话。</summary>
public sealed class RelaySessionHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RelayClient> _clients = new(StringComparer.Ordinal);
    private AppSettings? _settings;
    private CancellationToken _hubCt;
    private Func<CancellationToken, Task<TcpClient?>>? _localAcceptDial;

    public void SetLocalAcceptDial(Func<CancellationToken, Task<TcpClient?>>? dial)
    {
        lock (_gate)
        {
            _localAcceptDial = dial;
            foreach (var client in _clients.Values)
                client.SetLocalAcceptDial(dial);
        }
    }

    /// <summary>按电脑名找信令房间里的设备。在线优先。</summary>
    public bool TryFindDevice(string name, out RelayClient? client, out RelayDiscoveredDevice? device)
    {
        client = null;
        device = null;
        var key = (name ?? "").Trim();
        if (key.Length == 0) return false;

        RelayClient[] snapshot;
        lock (_gate)
            snapshot = _clients.Values.ToArray();

        foreach (var c in snapshot)
        {
            if (!c.TryGetDeviceByName(key, out var found) || found == null) continue;
            client = c;
            device = found;
            if (found.Online) return true;
        }

        return client != null;
    }

    public void AttachSettings(AppSettings settings) => _settings = settings;

    public void SetHubCancellation(CancellationToken hubCt) => _hubCt = hubCt;

    public async Task<RelayConnectResult> EnsureAsync(ConnectionEndpoint ep, AppSettings settings, CancellationToken ct)
    {
        settings.EnsureCollections();
        _settings = settings;

        var raw = (ep.Address ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return RelayConnectResult.Fail("地址为空");

        // hello.tcpPort 用本机 Peer 口；WS URL 默认边缘口 8443（与设置里 TCP 15155 无关）
        var peerTcpPort = settings.TcpPort > 0 ? settings.TcpPort : 15155;
        var wsUrl = RelayClient.NormalizeWebSocketUrl(raw, RelayClient.DefaultEdgePort);
        if (string.IsNullOrWhiteSpace(wsUrl))
            return RelayConnectResult.Fail("无法解析中转 WebSocket 地址（请用 ws://host:8443 或 host:8443）");

        var roomKey = string.IsNullOrWhiteSpace(settings.SecurityKey) ? "default" : settings.SecurityKey.Trim();
        var deviceId = settings.EnsureRelayDeviceId();

        var client = GetOrCreateClient(ep.Id);
        client.Configure(wsUrl, roomKey, deviceId, peerTcpPort, deviceId);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _hubCt);
        var result = await client.EnsureAsync(linked.Token).ConfigureAwait(false);
        ApplyToEndpoint(ep, result);
        SyncEndpointInSettings(ep);
        return result;
    }

    public async Task StartAllConfiguredAsync(AppSettings settings, CancellationToken ct)
    {
        settings.EnsureCollections();
        _settings = settings;
        SetHubCancellation(ct);

        foreach (var ep in settings.ConnectionEndpoints ?? [])
        {
            if (ep.Kind != ConnectionEndpoint.KindRelay) continue;
            if (string.IsNullOrWhiteSpace(ep.Address)) continue;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _hubCt);
                var result = await EnsureAsync(ep, settings, linked.Token).ConfigureAwait(false);
                ApplyToEndpoint(ep, result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                // 启动期失败不阻断整网；用户可用 ⚡ 重试
            }
        }
    }

    public async Task RebindRoomKeyAsync(AppSettings settings, CancellationToken ct)
    {
        settings.EnsureCollections();
        _settings = settings;
        var roomKey = string.IsNullOrWhiteSpace(settings.SecurityKey) ? "default" : settings.SecurityKey.Trim();
        var deviceId = settings.EnsureRelayDeviceId();
        var tcpPort = settings.TcpPort > 0 ? settings.TcpPort : 15155;

        RelayClient[] snapshot;
        lock (_gate)
            snapshot = _clients.Values.ToArray();

        foreach (var client in snapshot)
        {
            try
            {
                await client.RebindRoomKeyAsync(roomKey, deviceId, tcpPort, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* ignore */ }
        }

        lock (_gate)
        {
            foreach (var ep in settings.ConnectionEndpoints ?? [])
            {
                if (ep.Kind != ConnectionEndpoint.KindRelay) continue;
                if (!_clients.TryGetValue(ep.Id, out var c)) continue;
                ApplyToEndpoint(ep, c.SnapshotResult(true, "房间密钥已更新"));
            }
        }
    }

    public void StopAll()
    {
        RelayClient[] snapshot;
        lock (_gate)
        {
            snapshot = _clients.Values.ToArray();
            _clients.Clear();
        }

        foreach (var c in snapshot)
        {
            try { c.Stop(); } catch { /* ignore */ }
            _ = c.DisposeAsync();
        }
    }

    private RelayClient GetOrCreateClient(string endpointId)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(endpointId, out var existing))
                return existing;

            var client = new RelayClient(endpointId);
            client.SetLocalAcceptDial(_localAcceptDial);
            client.DevicesChanged += OnClientDevicesChanged;
            _clients[endpointId] = client;
            return client;
        }
    }

    private void OnClientDevicesChanged(string endpointId, IReadOnlyList<RelayDiscoveredDevice> devices, int? rttMs)
    {
        var settings = _settings;
        if (settings == null) return;

        var ep = settings.ConnectionEndpoints?.FirstOrDefault(e =>
            string.Equals(e.Id, endpointId, StringComparison.Ordinal));
        if (ep == null) return;

        ep.DiscoveredDevices = devices.ToList();
        if (rttMs is int ms) ep.LastRttMs = ms;
        ep.State = ConnectionEndpoint.StateConnected;
        ep.LastError = "";
    }

    private static void ApplyToEndpoint(ConnectionEndpoint ep, RelayConnectResult result)
    {
        if (result.Ok)
        {
            ep.LastError = "";
            ep.LastRttMs = result.RttMs;
            ep.VerifiedPeerName = "";
            ep.DiscoveredDevices = result.Devices.ToList();
            ep.State = ConnectionEndpoint.StateConnected;
        }
        else
        {
            ep.LastError = result.Message;
            ep.State = ConnectionEndpoint.StateError;
        }
    }

    private void SyncEndpointInSettings(ConnectionEndpoint ep)
    {
        var settings = _settings;
        if (settings?.ConnectionEndpoints == null) return;
        var idx = settings.ConnectionEndpoints.FindIndex(e =>
            string.Equals(e.Id, ep.Id, StringComparison.Ordinal));
        if (idx < 0) return;
        settings.ConnectionEndpoints[idx] = ep;
    }
}
