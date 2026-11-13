using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FlyScreen.Models;

namespace FlyScreen.Services.Network.Relay;

/// <summary>单个中转入口的长连接：hello 注册、保活、设备列表推送。</summary>
public sealed class RelayClient : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

    private readonly string _endpointId;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly object _stateGate = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private string _wsUrl = "";
    private string _roomKey = "default";
    private string _deviceId = "";
    private int _tcpPort = 15155;
    private string _localDeviceId = "";

    private volatile bool _registered;
    private volatile string _lastError = "";
    private int? _lastRttMs;
    private List<RelayDiscoveredDevice> _devices = [];
    private Func<CancellationToken, Task<TcpClient?>>? _localAcceptDial;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private readonly ConcurrentDictionary<string, List<RelayAddressCandidate>> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RelayPipe> _pipes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _readyWaits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _accepting = new(StringComparer.OrdinalIgnoreCase);

    public string EndpointId => _endpointId;

    public event Action<string, IReadOnlyList<RelayDiscoveredDevice>, int?>? DevicesChanged;

    public RelayClient(string endpointId) => _endpointId = endpointId;

    public bool IsConnected => _registered;

    public void SetLocalAcceptDial(Func<CancellationToken, Task<TcpClient?>>? dial) => _localAcceptDial = dial;

    public bool TryGetDeviceByName(string name, out RelayDiscoveredDevice? device)
    {
        device = null;
        var key = (name ?? "").Trim();
        if (key.Length == 0) return false;
        List<RelayDiscoveredDevice> list;
        lock (_stateGate)
            list = _devices.ToList();
        device = list.FirstOrDefault(d => string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));
        return device != null;
    }

    /// <summary>信令登记的 P2P 候选（host / ipv6 / srflx），不含中继入口本身。</summary>
    public IReadOnlyList<(string Ip, int Port)> ListP2PTargets(string deviceId)
    {
        var list = new List<(string Ip, int Port)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RelayDiscoveredDevice? device;
        RelayAddressCandidate[] candidateSnap;
        lock (_stateGate)
        {
            device = _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            // 与 MapDevice 写 _candidates 同锁，避免 ToArray 时列表被替换/改写
            candidateSnap = _candidates.TryGetValue(deviceId, out var candidates) && candidates is { Count: > 0 }
                ? candidates.ToArray()
                : [];
        }
        var fallbackPort = device?.TcpPort is int tcp && tcp is > 0 and <= 65535 ? tcp : 0;

        void Add(IPAddress ip, int port)
        {
            if (port is <= 0 or > 65535) return;
            if (IPAddress.IsLoopback(ip)) return;
            if (port == DefaultEdgePort) return;
            var key = $"{ip}:{port}";
            if (!seen.Add(key)) return;
            list.Add((ip.ToString(), port));
        }

        var ranked = new List<(int Rank, IPAddress Ip, int Port)>();
        foreach (var c in candidateSnap)
        {
            if (string.Equals(c.Kind, "relay", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryParseEndpoint(c.Address, fallbackPort, out var ip, out var port)) continue;
            ranked.Add((RankKind(c.Kind, ip), ip, port));
        }

        if (fallbackPort > 0
            && !string.IsNullOrWhiteSpace(device?.RemoteAddress)
            && TryParseEndpoint(device.RemoteAddress, fallbackPort, out var remoteIp, out _))
        {
            ranked.Add((RankKind("srflx", remoteIp), remoteIp, fallbackPort));
        }

        foreach (var item in ranked.OrderBy(x => x.Rank).ThenBy(x => x.Ip.ToString(), StringComparer.Ordinal))
            Add(item.Ip, item.Port);
        return list;
    }

    /// <summary>P2P 失败后打开中继，返回已接到字节管道上的本机 TcpClient（交给 PeerSession）。</summary>
    public async Task<TcpClient?> OpenOutboundTunnelAsync(string peerDeviceId, CancellationToken ct)
    {
        if (!_registered || _socket == null || string.IsNullOrWhiteSpace(peerDeviceId))
            return null;

        var sid = Guid.NewGuid().ToString("N");
        var wait = ArmReady(sid);
        try
        {
            await SendAsync(_socket, new RelayWireMessage
            {
                Type = RelayMessageTypes.RelayOpen,
                PeerId = peerDeviceId,
                SessionId = sid
            }, ct).ConfigureAwait(false);

            var ready = await WaitReadyAsync(wait, sid, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            if (!ready) return null;
            return await SpliceLoopbackAsync(sid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _readyWaits.TryRemove(sid, out _);
            return null;
        }
        catch
        {
            _readyWaits.TryRemove(sid, out _);
            return null;
        }
    }

    public void Configure(string wsUrl, string roomKey, string deviceId, int tcpPort, string localRelayDeviceId)
    {
        lock (_stateGate)
        {
            _wsUrl = wsUrl;
            _roomKey = roomKey;
            _deviceId = deviceId;
            _tcpPort = tcpPort;
            _localDeviceId = localRelayDeviceId;
        }
    }

    public async Task<RelayConnectResult> EnsureAsync(CancellationToken ct)
    {
        string wsUrl;
        lock (_stateGate)
        {
            wsUrl = _wsUrl;
        }

        if (string.IsNullOrWhiteSpace(wsUrl))
            return RelayConnectResult.Fail("地址为空");

        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_registered)
                return SnapshotResult(true, "已连接");

            // 用户点 ⚡ / 改地址后必须清掉上次失败，并重启会话；否则 WaitForReady 会立刻返回陈旧错误。
            _lastError = "";
            await RestartRunnerAsync().ConfigureAwait(false);
            return await WaitForReadyAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    public async Task RebindRoomKeyAsync(string roomKey, string deviceId, int tcpPort, CancellationToken ct)
    {
        string wsUrl;
        lock (_stateGate) { wsUrl = _wsUrl; }
        Configure(wsUrl, roomKey, deviceId, tcpPort, deviceId);
        await StopSessionAsync().ConfigureAwait(false);
        _registered = false;
        _devices = [];
        StartRunnerIfNeeded();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(HandshakeTimeout);
        try { await WaitForReadyAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* best effort */ }
    }

    public void Stop()
    {
        _ = StopSessionAsync();
    }

    public RelayConnectResult SnapshotResult(bool ok, string message)
    {
        List<RelayDiscoveredDevice> list;
        int? rtt;
        lock (_stateGate)
        {
            list = _devices.Select(CloneDevice).ToList();
            rtt = _lastRttMs;
            if (!ok && string.IsNullOrWhiteSpace(message) && !string.IsNullOrWhiteSpace(_lastError))
                message = _lastError;
        }

        return new RelayConnectResult(ok, message, rtt, list);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
        _ensureLock.Dispose();
    }

    private void StartRunnerIfNeeded()
    {
        if (_runTask is { IsCompleted: false }) return;
        StartRunner();
    }

    private void StartRunner()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        _runTask = Task.Run(() => RunLoopAsync(token), token);
    }

    private async Task RestartRunnerAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
        _lastError = "";
        StartRunner();
    }

    private async Task StopSessionAsync()
    {
        var cts = _runCts;
        _runCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }

        var task = _runTask;
        _runTask = null;
        if (task != null)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _registered = false;
    }

    private async Task<RelayConnectResult> WaitForReadyAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < HandshakeTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (_registered)
                return SnapshotResult(true, "已注册");
            // 仅在 Runner 已跑过一轮并留下错误时返回；启动瞬间的空窗继续等
            if (!string.IsNullOrWhiteSpace(_lastError) && _runTask != null)
                return SnapshotResult(false, _lastError);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (_registered)
            return SnapshotResult(true, "已注册");

        return SnapshotResult(false, string.IsNullOrWhiteSpace(_lastError) ? "注册超时" : _lastError);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _registered = false;
                _lastError = ex.Message;
            }

            attempt++;
            var delay = TimeSpan.FromMilliseconds(Math.Min(
                MaxReconnectDelay.TotalMilliseconds,
                BaseReconnectDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 6))));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        string wsUrl, roomKey, deviceId, localId;
        int tcpPort;
        lock (_stateGate)
        {
            wsUrl = _wsUrl;
            roomKey = _roomKey;
            deviceId = _deviceId;
            tcpPort = _tcpPort;
            localId = _localDeviceId;
        }

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"WebSocket 连接失败：{ex.Message}", ex);
        }

        _lastError = "";
        _registered = false;
        _socket = socket;
        try
        {
            await ReceiveWelcomeAsync(socket, ct).ConfigureAwait(false);

            var candidates = CollectLocalCandidates(tcpPort);
            var hello = new RelayWireMessage
            {
                Type = RelayMessageTypes.Hello,
                DeviceId = deviceId,
                Name = LocalPeerIdentity.MachineName,
                RoomKey = roomKey,
                Platform = LocalPeerIdentity.Platform,
                TcpPort = tcpPort > 0 ? tcpPort : null,
                Candidates = candidates.Count > 0 ? candidates : null
            };
            await SendAsync(socket, hello, ct).ConfigureAwait(false);

            await WaitRegisteredAsync(socket, ct).ConfigureAwait(false);
            _registered = true;
            _lastError = "";
            if (candidates.Count > 0)
            {
                await SendAsync(socket, new RelayWireMessage
                {
                    Type = RelayMessageTypes.Candidates,
                    Candidates = candidates
                }, ct).ConfigureAwait(false);
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pingTask = PingLoopAsync(socket, sessionCts.Token);
            try
            {
                await ReceiveLoopAsync(socket, localId, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                sessionCts.Cancel();
                try { await pingTask.ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
        finally
        {
            _registered = false;
            if (ReferenceEquals(_socket, socket))
                _socket = null;
            FailPipes();
        }
    }

    private static async Task ReceiveWelcomeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var msg = await ReceiveJsonAsync(socket, timeout.Token).ConfigureAwait(false);
        if (!string.Equals(msg?.Type, RelayMessageTypes.Welcome, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("未收到 welcome");
    }

    private async Task WaitRegisteredAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        while (!timeout.IsCancellationRequested)
        {
            var msg = await ReceiveJsonAsync(socket, timeout.Token).ConfigureAwait(false);
            if (msg == null) throw new InvalidOperationException("连接已关闭");
            if (string.Equals(msg.Type, RelayMessageTypes.Error, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg.Message) ? msg.Code ?? "error" : msg.Message);
            if (string.Equals(msg.Type, RelayMessageTypes.Registered, StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Devices is { Count: > 0 })
                    ApplyDeviceList(msg.Devices, _localDeviceId);
                return;
            }

            if (string.Equals(msg.Type, RelayMessageTypes.Devices, StringComparison.OrdinalIgnoreCase))
                ApplyDeviceList(msg.Devices, _localDeviceId);
        }

        throw new OperationCanceledException();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, string localId, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var raw = await ReceiveRawAsync(socket, ct).ConfigureAwait(false);
            if (raw == null) break;
            if (raw.Value.Type == WebSocketMessageType.Binary)
            {
                DispatchBinary(raw.Value.Data);
                continue;
            }

            if (raw.Value.Type != WebSocketMessageType.Text || raw.Value.Data.Length == 0)
                continue;

            RelayWireMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<RelayWireMessage>(
                    Encoding.UTF8.GetString(raw.Value.Data), RelayWire.JsonOptions);
            }
            catch
            {
                continue;
            }

            if (msg == null || string.IsNullOrWhiteSpace(msg.Type)) continue;

            switch (msg.Type.ToLowerInvariant())
            {
                case RelayMessageTypes.Devices:
                    ApplyDeviceList(msg.Devices, localId);
                    break;
                case RelayMessageTypes.DeviceUpdated:
                    if (msg.Device != null)
                        ApplyDevicePatch(msg.Device, localId);
                    break;
                case RelayMessageTypes.Signal:
                    if (string.Equals(msg.MsgType, "relay-request", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(msg.From)
                        && !string.IsNullOrWhiteSpace(msg.SessionId))
                    {
                        var from = msg.From;
                        var sid = msg.SessionId;
                        _ = Task.Run(() => AcceptIncomingRelayAsync(from, sid, ct));
                    }

                    break;
                case RelayMessageTypes.RelayReady:
                    if (!string.IsNullOrWhiteSpace(msg.SessionId)
                        && _readyWaits.TryRemove(msg.SessionId, out var ready))
                        ready.TrySetResult(true);
                    break;
                case RelayMessageTypes.RelayPeerLeft:
                    if (!string.IsNullOrWhiteSpace(msg.SessionId)
                        && _pipes.TryGetValue(msg.SessionId, out var left))
                        ClosePipe(left, notify: false);
                    break;
                case RelayMessageTypes.Pong:
                    if (msg.Ts is long sent)
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var rtt = (int)Math.Max(1, now - sent);
                        _lastRttMs = rtt;
                        NotifyDevicesChanged();
                    }

                    break;
                case RelayMessageTypes.Error:
                    if (!string.IsNullOrWhiteSpace(msg.SessionId)
                        && _readyWaits.TryRemove(msg.SessionId, out var failed))
                        failed.TrySetResult(false);
                    else if (string.Equals(msg.Code, "relay_open_failed", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(msg.Code, "peer_offline", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var kv in _readyWaits.ToArray())
                        {
                            if (_readyWaits.TryRemove(kv.Key, out var wait))
                                wait.TrySetResult(false);
                        }
                    }
                    else if (!_registered)
                        _lastError = string.IsNullOrWhiteSpace(msg.Message) ? msg.Code ?? "error" : msg.Message;
                    break;
            }
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
                if (socket.State != WebSocketState.Open) break;
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await SendAsync(socket, new RelayWireMessage { Type = RelayMessageTypes.Ping, Ts = ts }, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
        }
    }

    private void ApplyDeviceList(List<RelayDeviceDto>? dtos, string localDeviceId)
    {
        if (dtos == null) return;
        var filtered = dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Where(d => !string.Equals(d.Id, localDeviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        lock (_stateGate)
        {
            _devices = filtered
                .Select(MapDevice)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        NotifyDevicesChanged();
    }

    private void ApplyDevicePatch(RelayDeviceDto dto, string localDeviceId)
    {
        if (string.Equals(dto.Id, localDeviceId, StringComparison.OrdinalIgnoreCase)) return;
        lock (_stateGate)
        {
            var idx = _devices.FindIndex(d => string.Equals(d.Id, dto.Id, StringComparison.OrdinalIgnoreCase));
            if (!dto.Online)
            {
                if (idx >= 0) _devices.RemoveAt(idx);
            }
            else if (idx >= 0)
            {
                _devices[idx] = MapDevice(dto);
            }
            else if (!string.IsNullOrWhiteSpace(dto.Name))
            {
                _devices.Add(MapDevice(dto));
                _devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        NotifyDevicesChanged();
    }

    private void NotifyDevicesChanged()
    {
        List<RelayDiscoveredDevice> list;
        int? rtt;
        lock (_stateGate)
        {
            list = _devices.Select(CloneDevice).ToList();
            rtt = _lastRttMs;
        }

        try { DevicesChanged?.Invoke(_endpointId, list, rtt); } catch { /* ignore */ }
    }

    private RelayDiscoveredDevice MapDevice(RelayDeviceDto d)
    {
        // 调用方须已持有 _stateGate。拷贝候选列表，避免与 ListP2PTargets 共享可变实例。
        if (!string.IsNullOrWhiteSpace(d.Id))
            _candidates[d.Id] = d.Candidates is { Count: > 0 } ? d.Candidates.ToList() : [];
        return new RelayDiscoveredDevice
        {
            Id = d.Id,
            Name = d.Name,
            Online = d.Online,
            TcpPort = d.TcpPort,
            RemoteAddress = d.RemoteAddress
        };
    }

    private static RelayDiscoveredDevice CloneDevice(RelayDiscoveredDevice d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Online = d.Online,
        RttMs = d.RttMs,
        TcpPort = d.TcpPort,
        RemoteAddress = d.RemoteAddress
    };

    private async Task SendAsync(ClientWebSocket socket, RelayWireMessage msg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(msg, RelayWire.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<RelayWireMessage?> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (true)
        {
            var raw = await ReceiveRawAsync(socket, ct).ConfigureAwait(false);
            if (raw == null) return null;
            if (raw.Value.Type != WebSocketMessageType.Text || raw.Value.Data.Length == 0)
                continue;
            return JsonSerializer.Deserialize<RelayWireMessage>(
                Encoding.UTF8.GetString(raw.Value.Data), RelayWire.JsonOptions);
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveRawAsync(
        ClientWebSocket socket, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var type = WebSocketMessageType.Text;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            type = result.MessageType;
            if (result.Count > 0)
                ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
            if (ms.Length > 2_000_000)
                throw new InvalidOperationException("消息过大");
        }

        return (type, ms.ToArray());
    }

    private async Task AcceptIncomingRelayAsync(string fromDeviceId, string sessionId, CancellationToken ct)
    {
        if (!_accepting.TryAdd(sessionId, 0)) return;
        var wait = ArmReady(sessionId);
        try
        {
            var socket = _socket;
            if (socket == null || !_registered) return;
            await SendAsync(socket, new RelayWireMessage
            {
                Type = RelayMessageTypes.RelayOpen,
                PeerId = fromDeviceId,
                SessionId = sessionId
            }, ct).ConfigureAwait(false);

            var ready = await WaitReadyAsync(wait, sessionId, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            if (!ready) return;

            var dial = _localAcceptDial;
            if (dial == null)
            {
                await SendCloseAsync(sessionId, ct).ConfigureAwait(false);
                return;
            }

            TcpClient? local;
            try { local = await dial(ct).ConfigureAwait(false); }
            catch { local = null; }

            if (local == null || !local.Connected)
            {
                try { local?.Dispose(); } catch { /* ignore */ }
                await SendCloseAsync(sessionId, ct).ConfigureAwait(false);
                return;
            }

            StartPipe(sessionId, local, ct);
        }
        catch (OperationCanceledException)
        {
            _readyWaits.TryRemove(sessionId, out _);
        }
        catch
        {
            _readyWaits.TryRemove(sessionId, out _);
        }
        finally
        {
            _accepting.TryRemove(sessionId, out _);
        }
    }

    private async Task<TcpClient?> SpliceLoopbackAsync(string sessionId, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        TcpClient? client = null;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(ct).AsTask();
            client = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            var accepted = await acceptTask.ConfigureAwait(false);
            accepted.NoDelay = true;
            StartPipe(sessionId, accepted, ct);
            var ready = client;
            client = null;
            return ready;
        }
        catch
        {
            try { client?.Dispose(); } catch { /* ignore */ }
            await SendCloseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        finally
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
    }

    private void StartPipe(string sessionId, TcpClient tcp, CancellationToken sessionCt)
    {
        if (!Guid.TryParseExact(sessionId, "N", out var guid))
        {
            try { tcp.Dispose(); } catch { /* ignore */ }
            return;
        }

        var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        var pipe = new RelayPipe(sessionId, guid, tcp, pipeCts);
        if (!_pipes.TryAdd(sessionId, pipe))
        {
            pipeCts.Dispose();
            try { tcp.Dispose(); } catch { /* ignore */ }
            return;
        }

        _ = Task.Run(() => PumpPipeAsync(pipe));
    }

    private async Task PumpPipeAsync(RelayPipe pipe)
    {
        var ct = pipe.Cts.Token;
        try
        {
            var stream = pipe.Tcp.GetStream();
            var readTask = Task.Run(async () =>
            {
                var buf = new byte[64 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    await SendBinaryAsync(pipe.SessionGuid, buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }, ct);

            var writeTask = Task.Run(async () =>
            {
                await foreach (var chunk in pipe.Incoming.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                }
            }, ct);

            await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
        }
        catch
        {
            // 对端或本机套接字已关闭
        }
        finally
        {
            ClosePipe(pipe, notify: true);
        }
    }

    private void DispatchBinary(byte[] data)
    {
        if (data.Length < 17 || data[0] != 0x01) return;
        Guid guid;
        try { guid = new Guid(data.AsSpan(1, 16)); }
        catch { return; }

        var sid = guid.ToString("N");
        if (!_pipes.TryGetValue(sid, out var pipe)) return;
        var payload = new byte[data.Length - 17];
        Buffer.BlockCopy(data, 17, payload, 0, payload.Length);
        pipe.Incoming.Writer.TryWrite(payload);
    }

    private async Task SendBinaryAsync(Guid session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open) return;
        var frame = new byte[17 + payload.Length];
        frame[0] = 0x01;
        session.TryWriteBytes(frame.AsSpan(1, 16));
        payload.Span.CopyTo(frame.AsSpan(17));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendCloseAsync(string sessionId, CancellationToken ct)
    {
        var socket = _socket;
        if (socket == null) return;
        try
        {
            await SendAsync(socket, new RelayWireMessage
            {
                Type = RelayMessageTypes.RelayClose,
                SessionId = sessionId
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // 连接已断
        }
    }

    private TaskCompletionSource<bool> ArmReady(string sessionId)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readyWaits[sessionId] = tcs;
        return tcs;
    }

    private async Task<bool> WaitReadyAsync(
        TaskCompletionSource<bool> wait, string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var completed = await Task.WhenAny(wait.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                .ConfigureAwait(false);
            if (completed != wait.Task)
            {
                _readyWaits.TryRemove(sessionId, out _);
                return false;
            }

            return await wait.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _readyWaits.TryRemove(sessionId, out _);
            return false;
        }
    }

    private void ClosePipe(RelayPipe pipe, bool notify)
    {
        if (!_pipes.TryRemove(pipe.SessionId, out _)) return;
        try { pipe.Incoming.Writer.TryComplete(); } catch { /* ignore */ }
        try { pipe.Tcp.Close(); } catch { /* ignore */ }
        try { pipe.Cts.Cancel(); } catch { /* ignore */ }
        if (notify)
            _ = SendCloseAsync(pipe.SessionId, CancellationToken.None);
    }

    private void FailPipes()
    {
        foreach (var kv in _readyWaits.ToArray())
        {
            if (_readyWaits.TryRemove(kv.Key, out var wait))
                wait.TrySetResult(false);
        }

        foreach (var pipe in _pipes.Values.ToArray())
            ClosePipe(pipe, notify: false);
    }

    private static int RankKind(string? kind, IPAddress ip)
    {
        var v4 = ip.AddressFamily == AddressFamily.InterNetwork;
        var priv = v4 && IsPrivateOrLocal(ip);
        if (string.Equals(kind, "host", StringComparison.OrdinalIgnoreCase) && priv) return 0;
        if (string.Equals(kind, "ipv6", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(kind, "host", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(kind, "srflx", StringComparison.OrdinalIgnoreCase)) return 3;
        return priv ? 2 : 4;
    }

    private static bool IsPrivateOrLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        return false;
    }

    internal static bool TryParseEndpoint(string? address, int fallbackPort, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = 0;
        address = (address ?? "").Trim();
        if (address.Length == 0) return false;

        if (address.StartsWith('['))
        {
            var end = address.IndexOf(']');
            if (end <= 1) return false;
            if (!IPAddress.TryParse(address[1..end], out ip!)) return false;
            if (address.Length > end + 1 && address[end + 1] == ':')
            {
                if (!int.TryParse(address[(end + 2)..], out port)) return false;
            }
            else port = fallbackPort;
            return port is > 0 and <= 65535;
        }

        var colon = address.LastIndexOf(':');
        if (colon > 0
            && IPAddress.TryParse(address[..colon], out ip!)
            && int.TryParse(address[(colon + 1)..], out port))
            return port is > 0 and <= 65535;

        if (IPAddress.TryParse(address, out ip!))
        {
            port = fallbackPort;
            return port is > 0 and <= 65535;
        }

        return false;
    }

    private static List<RelayAddressCandidate> CollectLocalCandidates(int tcpPort)
    {
        var list = new List<RelayAddressCandidate>();
        if (tcpPort is <= 0 or > 65535) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                foreach (var uni in props.UnicastAddresses)
                {
                    var addr = uni.Address;
                    if (addr == null || IPAddress.IsLoopback(addr)) continue;
                    if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast) continue;
                    }
                    else if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var b = addr.GetAddressBytes();
                        if (b[0] == 0 || b[0] >= 224) continue;
                        if (b[0] == 169 && b[1] == 254) continue;
                    }
                    else continue;

                    var text = addr.AddressFamily == AddressFamily.InterNetworkV6
                        ? $"[{addr}]:{tcpPort}"
                        : $"{addr}:{tcpPort}";
                    if (!seen.Add(text)) continue;
                    var kind = addr.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "host";
                    list.Add(new RelayAddressCandidate
                    {
                        Kind = kind,
                        Address = text,
                        Priority = list.Count * 10
                    });
                }
            }
        }
        catch
        {
            return list;
        }

        return list;
    }

    private sealed class RelayPipe
    {
        public RelayPipe(string sessionId, Guid sessionGuid, TcpClient tcp, CancellationTokenSource cts)
        {
            SessionId = sessionId;
            SessionGuid = sessionGuid;
            Tcp = tcp;
            Cts = cts;
        }

        public string SessionId { get; }
        public Guid SessionGuid { get; }
        public TcpClient Tcp { get; }
        public CancellationTokenSource Cts { get; }
        public Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>中继边缘 WebSocket 默认端口（与 FlyScreen.Relay EdgeListen 一致；勿与 Peer TCP 15155 混淆）。</summary>
    public const int DefaultEdgePort = 8443;

    public static string NormalizeWebSocketUrl(string raw, int defaultEdgePort = DefaultEdgePort)
    {
        raw = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var edgePort = defaultEdgePort > 0 ? defaultEdgePort : DefaultEdgePort;
        var trimmed = raw.TrimEnd('/');
        if (trimmed.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            // 已是完整 ws(s) URL
            if (trimmed.Contains("://", StringComparison.Ordinal))
                return trimmed;
            // 形如 host:8443/ws
            if (ConnectionEndpointProbe.TryParseHostPort(trimmed[..^3].TrimEnd('/'), out var h0, out var p0, edgePort))
                return $"ws://{h0}:{p0}/ws";
            return trimmed;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == "ws" || uri.Scheme == "wss" || uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var scheme = uri.Scheme is "https" or "wss" ? "wss" : "ws";
            var path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? "/ws"
                : uri.AbsolutePath.TrimEnd('/') + "/ws";
            var builder = new UriBuilder(uri) { Scheme = scheme, Path = path };
            // 无显式端口时 UriBuilder 可能落到 80/443；边缘默认 8443
            if (uri.IsDefaultPort)
                builder.Port = edgePort;
            return builder.Uri.ToString().TrimEnd('/');
        }

        if (ConnectionEndpointProbe.TryParseHostPort(raw, out var host, out var port, edgePort))
            return $"ws://{host}:{port}/ws";

        return "";
    }
}

public sealed record RelayConnectResult(bool Ok, string Message, int? RttMs, IReadOnlyList<RelayDiscoveredDevice> Devices)
{
    public static RelayConnectResult Fail(string message)
        => new(false, message, null, Array.Empty<RelayDiscoveredDevice>());
}
