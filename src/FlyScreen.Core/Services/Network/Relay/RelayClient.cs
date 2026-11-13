using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

    public string EndpointId => _endpointId;

    public event Action<string, IReadOnlyList<RelayDiscoveredDevice>, int?>? DevicesChanged;

    public RelayClient(string endpointId) => _endpointId = endpointId;

    public bool IsConnected => _registered;

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
            {
                return SnapshotResult(true, "已连接");
            }

            StartRunnerIfNeeded();
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

        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        _runTask = Task.Run(() => RunLoopAsync(token), token);
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
            if (!string.IsNullOrWhiteSpace(_lastError))
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

        await ReceiveWelcomeAsync(socket, ct).ConfigureAwait(false);

        var hello = new RelayWireMessage
        {
            Type = RelayMessageTypes.Hello,
            DeviceId = deviceId,
            Name = LocalPeerIdentity.MachineName,
            RoomKey = roomKey,
            Platform = LocalPeerIdentity.Platform,
            TcpPort = tcpPort > 0 ? tcpPort : null
        };
        await SendAsync(socket, hello, ct).ConfigureAwait(false);

        await WaitRegisteredAsync(socket, ct).ConfigureAwait(false);
        _registered = true;
        _lastError = "";

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

        _registered = false;
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
            var msg = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
            if (msg == null) break;

            switch (msg.Type.ToLowerInvariant())
            {
                case RelayMessageTypes.Devices:
                    ApplyDeviceList(msg.Devices, localId);
                    break;
                case RelayMessageTypes.DeviceUpdated:
                    if (msg.Device != null)
                        ApplyDevicePatch(msg.Device, localId);
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
        var mapped = dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Where(d => !string.Equals(d.Id, localDeviceId, StringComparison.OrdinalIgnoreCase))
            .Select(MapDevice)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_stateGate)
        {
            _devices = mapped;
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

    private static RelayDiscoveredDevice MapDevice(RelayDeviceDto d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Online = d.Online
    };

    private static RelayDiscoveredDevice CloneDevice(RelayDiscoveredDevice d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Online = d.Online,
        RttMs = d.RttMs
    };

    private static async Task SendAsync(ClientWebSocket socket, RelayWireMessage msg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(msg, RelayWire.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<RelayWireMessage?> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var seg = new ArraySegment<byte>(buffer);
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(seg, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        if (ms.Length == 0) return null;
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return JsonSerializer.Deserialize<RelayWireMessage>(json, RelayWire.JsonOptions);
    }

    public static string NormalizeWebSocketUrl(string raw, int defaultTcpPort)
    {
        raw = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var trimmed = raw.TrimEnd('/');
        if (trimmed.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == "ws" || uri.Scheme == "wss" || uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var scheme = uri.Scheme is "https" or "wss" ? "wss" : "ws";
            var path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? "/ws"
                : uri.AbsolutePath.TrimEnd('/') + "/ws";
            var builder = new UriBuilder(uri) { Scheme = scheme, Path = path };
            return builder.Uri.ToString().TrimEnd('/');
        }

        if (ConnectionEndpointProbe.TryParseHostPort(raw, out var host, out var port, defaultTcpPort))
            return $"ws://{host}:{port}/ws";

        return "";
    }
}

public sealed record RelayConnectResult(bool Ok, string Message, int? RttMs, IReadOnlyList<RelayDiscoveredDevice> Devices)
{
    public static RelayConnectResult Fail(string message)
        => new(false, message, null, Array.Empty<RelayDiscoveredDevice>());
}
