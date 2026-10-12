using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 双向连接管理：本机监听 + 主动连出。任意方向可用即可收发；
/// 出入站都通则标记「双向已连接」，并做心跳保活与断线重连。
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly DiscoveryService _discovery = new();
    private readonly ConcurrentDictionary<string, PeerLink> _links = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _bindIp = "0.0.0.0";

    public string LocalMachine { get; } = Environment.MachineName;
    public DiscoveryService Discovery => _discovery;
    public string BindIp => _bindIp;

    public event Action<string, bool, bool>? PeerLinkChanged;
    public event Action<string, NetMessage>? MessageReceived;
    public event Action<string>? StatusMessage;

    public ConnectionManager(AppSettings settings) => _settings = settings;

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts = new CancellationTokenSource();

        var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
        _bindIp = nic?.IPv4 ?? "0.0.0.0";
        if (nic != null)
        {
            _settings.PreferredLocalIp = nic.IPv4;
            _settings.PreferredNicId = nic.Id;
        }

        var localEp = _bindIp == "0.0.0.0"
            ? new IPEndPoint(IPAddress.Any, _settings.TcpPort)
            : new IPEndPoint(IPAddress.Parse(_bindIp), _settings.TcpPort);

        _listener = new TcpListener(localEp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        _discovery.AnnounceTcpPort = _settings.TcpPort;
        if (_bindIp != "0.0.0.0")
            _discovery.Start(_bindIp, _settings.UdpPort);

        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(ct), ct);
        _ = Task.Run(() => MaintainLoopAsync(ct), ct);
        StatusMessage?.Invoke($"已在 {_bindIp}:{_settings.TcpPort} 监听");
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _discovery.Stop();

        foreach (var link in _links.Values)
            await link.DisposeAsync().ConfigureAwait(false);
        _links.Clear();

        _listener = null;
        _cts = null;
    }

    public IEnumerable<string> ConfiguredPeers()
    {
        _settings.EnsureLocalMachineSlot();
        return _settings.MachineSlots
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Where(m => !string.Equals(m, LocalMachine, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsPeerReachable(string name)
        => _links.TryGetValue(name, out var link) && link.AnyAlive;

    public async Task<bool> SendToAsync(string peerName, MessageType type, byte[]? payload = null)
    {
        if (!_links.TryGetValue(peerName, out var link) || !link.AnyAlive)
            return false;

        try
        {
            await link.SendAsync(type, payload).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task BroadcastAsync(MessageType type, byte[]? payload = null)
    {
        foreach (var name in _links.Keys.ToArray())
            await SendToAsync(name, type, payload).ConfigureAwait(false);
    }

    public async Task ReconnectNowAsync()
    {
        if (_cts == null) return;
        foreach (var peer in ConfiguredPeers())
            await EnsureOutboundAsync(peer, _cts.Token).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var session = new PeerSession(client, isOutbound: false);
                WireSession(session);
                // 等待对端发 Handshake；超时丢弃
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    if (!session.Authenticated)
                        await session.DisposeAsync().ConfigureAwait(false);
                }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task MaintainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var peer in ConfiguredPeers())
                    await EnsureOutboundAsync(peer, ct).ConfigureAwait(false);

                foreach (var kv in _links.ToArray())
                    await kv.Value.HeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureOutboundAsync(string peerName, CancellationToken ct)
    {
        var link = _links.GetOrAdd(peerName, _ => new PeerLink(peerName));
        if (link.Outbound is { IsAlive: true, Authenticated: true })
            return;

        var ip = await ResolvePeerIpAsync(peerName, ct).ConfigureAwait(false);
        if (ip == null) return;

        if (_settings.SameSubnetOnly)
        {
            var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
            if (nic != null && !NicService.SameSubnet(nic.IPv4, nic.SubnetMask, ip))
                return;
        }

        try
        {
            var client = new TcpClient();
            if (_bindIp != "0.0.0.0")
                client.Client.Bind(new IPEndPoint(IPAddress.Parse(_bindIp), 0));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Parse(ip), _settings.TcpPort, timeoutCts.Token).ConfigureAwait(false);

            var session = new PeerSession(client, isOutbound: true);
            WireSession(session);

            var hs = MessageCodec.EncodeJson(MessageType.Handshake, new HandshakePayload
            {
                MachineName = LocalMachine,
                SecurityKey = _settings.SecurityKey,
                LocalIp = _bindIp
            });
            await session.SendRawAsync(hs).ConfigureAwait(false);

            var ackMsg = await WaitMessageAsync(session, MessageType.HandshakeAck, TimeSpan.FromSeconds(3), ct)
                .ConfigureAwait(false);
            if (ackMsg == null)
            {
                UnwireSession(session);
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var ack = MessageCodec.DecodeJson<HandshakePayload>(ackMsg.Value.Payload);
            if (ack == null || !ValidateKey(ack.SecurityKey))
            {
                UnwireSession(session);
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            session.SetPeerIdentity(peerName);
            await link.SetOutboundAsync(session).ConfigureAwait(false);
            NotifyLink(peerName, link);
            StatusMessage?.Invoke($"已出站连接 {peerName} ({ip})");
        }
        catch
        {
            // 下一轮重试
        }
    }

    private void WireSession(PeerSession session)
    {
        session.MessageReceived += OnSessionMessage;
        session.Disconnected += OnSessionDisconnected;
    }

    private void UnwireSession(PeerSession session)
    {
        session.MessageReceived -= OnSessionMessage;
        session.Disconnected -= OnSessionDisconnected;
    }

    private async void OnSessionMessage(PeerSession session, NetMessage msg)
    {
        try
        {
            switch (msg.Type)
            {
                case MessageType.Handshake when !session.IsOutbound:
                    await CompleteInboundHandshakeAsync(session, msg).ConfigureAwait(false);
                    return;
                case MessageType.Heartbeat:
                    await session.SendAsync(MessageType.HeartbeatAck).ConfigureAwait(false);
                    return;
                case MessageType.HeartbeatAck:
                case MessageType.HandshakeAck:
                    return;
            }

            if (!session.Authenticated || string.IsNullOrEmpty(session.PeerName))
                return;

            MessageReceived?.Invoke(session.PeerName, msg);
        }
        catch
        {
            // ignore
        }
    }

    private async Task CompleteInboundHandshakeAsync(PeerSession session, NetMessage msg)
    {
        if (session.Authenticated) return;

        var hs = MessageCodec.DecodeJson<HandshakePayload>(msg.Payload);
        if (hs == null || !ValidateKey(hs.SecurityKey) || string.IsNullOrWhiteSpace(hs.MachineName))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (_settings.SameSubnetOnly)
        {
            var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
            if (nic != null && !string.IsNullOrEmpty(hs.LocalIp) &&
                !NicService.SameSubnet(nic.IPv4, nic.SubnetMask, hs.LocalIp))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }

        var configured = ConfiguredPeers().Contains(hs.MachineName, StringComparer.OrdinalIgnoreCase);
        if (!configured && !_settings.MachineSlots.Any(string.IsNullOrWhiteSpace))
        {
            StatusMessage?.Invoke($"未授权主机尝试连接: {hs.MachineName}");
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // 自动填入空槽位
        if (!configured)
        {
            var idx = _settings.MachineSlots.FindIndex(string.IsNullOrWhiteSpace);
            if (idx >= 0) _settings.MachineSlots[idx] = hs.MachineName;
        }

        var ack = MessageCodec.EncodeJson(MessageType.HandshakeAck, new HandshakePayload
        {
            MachineName = LocalMachine,
            SecurityKey = _settings.SecurityKey,
            LocalIp = _bindIp,
            IsReply = true
        });
        await session.SendRawAsync(ack).ConfigureAwait(false);
        session.SetPeerIdentity(hs.MachineName);

        var link = _links.GetOrAdd(hs.MachineName, _ => new PeerLink(hs.MachineName));
        await link.SetInboundAsync(session).ConfigureAwait(false);
        NotifyLink(hs.MachineName, link);
        StatusMessage?.Invoke($"已入站连接 {hs.MachineName}");
    }

    private async void OnSessionDisconnected(PeerSession session, Exception? ex)
    {
        UnwireSession(session);
        if (!string.IsNullOrEmpty(session.PeerName) && _links.TryGetValue(session.PeerName, out var link))
        {
            await link.RemoveSessionAsync(session).ConfigureAwait(false);
            NotifyLink(session.PeerName, link);
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    private void NotifyLink(string name, PeerLink link)
        => PeerLinkChanged?.Invoke(name, link.Outbound is { IsAlive: true }, link.Inbound is { IsAlive: true });

    private bool ValidateKey(string? key)
        => !string.IsNullOrEmpty(key) &&
           string.Equals(key.Trim(), _settings.SecurityKey.Trim(), StringComparison.Ordinal);

    private async Task<string?> ResolvePeerIpAsync(string peerName, CancellationToken ct)
    {
        var discovered = _discovery.FindIp(peerName);
        if (discovered != null) return discovered;
        return await NicService.ResolveHostAsync(peerName, ct).ConfigureAwait(false);
    }

    private static async Task<NetMessage?> WaitMessageAsync(
        PeerSession session, MessageType type, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NetMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(PeerSession s, NetMessage m)
        {
            if (m.Type == type)
                tcs.TrySetResult(m);
        }

        session.MessageReceived += Handler;
        try
        {
            // 用 WhenAny 代替 CancelAfter，避免调试器刷 TaskCanceledException
            var delayTask = Task.Delay(timeout, ct);
            var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.IsCompletedSuccessfully)
                return tcs.Task.Result;
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            session.MessageReceived -= Handler;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class PeerLink
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public string Name { get; }
        public PeerSession? Outbound { get; private set; }
        public PeerSession? Inbound { get; private set; }
        public bool AnyAlive => Outbound is { IsAlive: true } || Inbound is { IsAlive: true };

        public PeerLink(string name) => Name = name;

        public async Task SetOutboundAsync(PeerSession session)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Outbound != null && !ReferenceEquals(Outbound, session))
                    await Outbound.DisposeAsync().ConfigureAwait(false);
                Outbound = session;
            }
            finally { _gate.Release(); }
        }

        public async Task SetInboundAsync(PeerSession session)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Inbound != null && !ReferenceEquals(Inbound, session))
                    await Inbound.DisposeAsync().ConfigureAwait(false);
                Inbound = session;
            }
            finally { _gate.Release(); }
        }

        public async Task RemoveSessionAsync(PeerSession session)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(Outbound, session)) Outbound = null;
                if (ReferenceEquals(Inbound, session)) Inbound = null;
            }
            finally { _gate.Release(); }
        }

        public async Task SendAsync(MessageType type, byte[]? payload)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            PeerSession? primary;
            PeerSession? secondary;
            try
            {
                var a = Outbound is { IsAlive: true } ? Outbound : null;
                var b = Inbound is { IsAlive: true } ? Inbound : null;
                if (a != null && b != null)
                {
                    if (a.LastRecvUtc >= b.LastRecvUtc) { primary = a; secondary = b; }
                    else { primary = b; secondary = a; }
                }
                else
                {
                    primary = a ?? b;
                    secondary = null;
                }
            }
            finally { _gate.Release(); }

            if (primary == null)
                throw new IOException("无可用连接");

            try
            {
                await primary.SendAsync(type, payload).ConfigureAwait(false);
            }
            catch (Exception) when (secondary != null)
            {
                await secondary.SendAsync(type, payload).ConfigureAwait(false);
            }
        }

        public async Task HeartbeatAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            PeerSession?[] sessions;
            try { sessions = [Outbound, Inbound]; }
            finally { _gate.Release(); }

            foreach (var s in sessions)
            {
                if (s is not { IsAlive: true, Authenticated: true }) continue;
                if ((DateTime.UtcNow - s.LastRecvUtc).TotalSeconds > 12)
                {
                    await s.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                try { await s.SendAsync(MessageType.Heartbeat).ConfigureAwait(false); }
                catch { /* ignore */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Outbound != null) await Outbound.DisposeAsync().ConfigureAwait(false);
                if (Inbound != null) await Inbound.DisposeAsync().ConfigureAwait(false);
                Outbound = null;
                Inbound = null;
            }
            finally { _gate.Release(); }
        }
    }
}
