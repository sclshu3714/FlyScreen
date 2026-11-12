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
    /// <summary>UDP/握手宣告用的具体 IPv4；「自动」监听时与 <see cref="_bindIp"/>（0.0.0.0）分离，避免出站误绑物理网卡。</summary>
    private string _announceIp = "0.0.0.0";
    private readonly ConcurrentDictionary<string, long> _layoutJoinUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _keyMismatchNotice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _connectFailNotice = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>设备管理直接投影等临时保活对端（不进布局/绑定）。</summary>
    private readonly ConcurrentDictionary<string, byte> _adHocPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _outboundBusy = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bulkBusy = new(StringComparer.OrdinalIgnoreCase);
    private long _lastDialTick;

    public string LocalMachine => LocalPeerIdentity.MachineName;
    public DiscoveryService Discovery => _discovery;
    public string BindIp => _bindIp;
    /// <summary>UDP/握手宣告用的本机 IPv4（自动监听时为优选物理网卡，非 VPN）。</summary>
    public string LocalAnnounceIp => AdvertisedLocalIp;

    public event Action<string, bool, bool>? PeerLinkChanged;
    public event Action<string, NetMessage>? MessageReceived;
    /// <summary>对端中途更新了身份/Caps（如安装工具后 PeerInfo）。</summary>
    public event Action<string>? PeerInfoUpdated;
    public event Action<string>? StatusMessage;
    /// <summary>入站握手密钥正确，且对端请求加入布局。返回 false 则拒绝连接。</summary>
    public Func<string, bool>? TryAcceptLayoutJoin;

    public ConnectionManager(AppSettings settings)
    {
        _settings = settings;
        _discovery.PeerDialRequested += OnPeerDialRequested;
    }

    /// <summary>监听与维护循环是否已在运行（未 Stop）。</summary>
    public bool IsRunning => _cts != null && _listener != null;

    /// <summary>
    /// 偏好网卡当前 IP 与运行中宣告/绑定 IP 是否不一致（换 Wi‑Fi / DHCP 后常见）。
    /// 为 true 时应 <see cref="StartAsync"/> 重绑，不能只 Refresh/Reconnect。
    /// </summary>
    public bool NeedsListenRebind()
    {
        if (!IsListening) return true;
        var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
        if (nic == null || string.IsNullOrWhiteSpace(nic.IPv4)) return false;

        var wantAny = NicService.IsAutomatic(nic);
        var listeningAny = string.IsNullOrWhiteSpace(_bindIp) || _bindIp == "0.0.0.0";
        if (wantAny) return !listeningAny;
        if (listeningAny) return true;
        return !string.Equals(_bindIp, nic.IPv4, StringComparison.OrdinalIgnoreCase);
    }

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts = new CancellationTokenSource();

        LocalPeerIdentity.SetMachineNameOverride(
            string.IsNullOrWhiteSpace(_settings.MachineNameOverride) ? null : _settings.MachineNameOverride);

        var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
        _bindIp = nic?.IPv4 ?? "0.0.0.0";
        if (nic != null)
        {
            _settings.PreferredLocalIp = nic.IPv4;
            _settings.PreferredNicId = nic.Id;
        }

        // Android 绑定单个 Wi‑Fi IP 时，宣告地址和真实出口经常不一致，入站会全部丢。监听所有接口。
        var listenAny = _bindIp == "0.0.0.0" || OperatingSystem.IsAndroid();
        var localEp = listenAny
            ? new IPEndPoint(IPAddress.Any, _settings.TcpPort)
            : new IPEndPoint(IPAddress.Parse(_bindIp), _settings.TcpPort);

        _listener = new TcpListener(localEp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        _discovery.AnnounceTcpPort = _settings.TcpPort;
        // TCP：自动时保持 _bindIp=0.0.0.0（出站不绑死 LAN，Tailscale 才能通）。
        // UDP 发现：宣告 IP 用物理网卡；bindAny 收包 + 全网卡（含 Tailscale）逐口广播，
        // 并对连接配置中的远程地址周期单播探测。
        _announceIp = _bindIp;
        var announceMask = nic?.SubnetMask;
        if (_announceIp == "0.0.0.0")
        {
            var announceNic = NicService.PickBestUsableNic();
            _announceIp = announceNic?.IPv4 ?? "0.0.0.0";
            announceMask = announceNic?.SubnetMask;
        }
        else if (string.IsNullOrWhiteSpace(announceMask) || announceMask == "0.0.0.0")
        {
            announceMask = NicService.FindByIpv4(_announceIp)?.SubnetMask ?? announceMask;
        }

        try
        {
            var discoveryBindAny = OperatingSystem.IsAndroid()
                                   || listenAny
                                   || string.Equals(_announceIp, "0.0.0.0", StringComparison.Ordinal);
            _discovery.Start(_announceIp, _settings.UdpPort, announceMask, discoveryBindAny,
                announceAllUsable: true);
            RefreshDiscoveryProbeHosts();
        }
        catch (SocketException ex)
        {
            StatusMessage?.Invoke(
                $"UDP 发现端口 {_settings.UdpPort} 绑定失败（{ex.SocketErrorCode}）。" +
                "请退出同机其它玄鸟实例，或在网络设置中更换发现端口后点「应用」。");
        }

        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(ct), ct);
        _ = Task.Run(() => MaintainLoopAsync(ct), ct);
        StatusMessage?.Invoke(listenAny
            ? $"已在 0.0.0.0:{_settings.TcpPort} 监听（全部网卡）"
            : $"已在 {_bindIp}:{_settings.TcpPort} 监听");
    }

    private string AdvertisedLocalIp
        => _bindIp == "0.0.0.0" || string.IsNullOrWhiteSpace(_bindIp) ? _announceIp : _bindIp;

    public async Task StopAsync()
    {
        // 先停监听唤醒 Accept，再取消 CTS，避免 AcceptTcpClientAsync(ct) 路径抛 OCE
        var listener = _listener;
        _listener = null;
        try { listener?.Stop(); } catch { /* ignore */ }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _discovery.Stop();

        await DropAllPeerLinksAsync().ConfigureAwait(false);
        _adHocPeers.Clear();

        _cts = null;
    }

    /// <summary>断开全部对端链路（保留发现/监听）。被踢出布局时使用。</summary>
    public async Task DropAllPeerLinksAsync()
    {
        foreach (var link in _links.Values.ToArray())
        {
            try { await link.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _links.Clear();
    }

    public IEnumerable<string> ConfiguredPeers()
    {
        _settings.EnsureLocalMachineSlot();
        // 布局槽位 + 远程绑定 + 临时投影保活对端
        var names = _settings.MachineSlots
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Where(m => !string.Equals(m, LocalMachine, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Trim())
            .ToList();

        foreach (var b in _settings.RemoteBindings ?? [])
        {
            if (string.IsNullOrWhiteSpace(b.BoundLocalDeviceId) || string.IsNullOrWhiteSpace(b.RemoteName))
                continue;
            var n = b.RemoteName.Trim();
            if (string.Equals(n, LocalMachine, StringComparison.OrdinalIgnoreCase))
                continue;
            names.Add(n);
        }

        foreach (var n in _adHocPeers.Keys)
        {
            if (string.Equals(n, LocalMachine, StringComparison.OrdinalIgnoreCase))
                continue;
            names.Add(n);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>设备管理直接投影：把对端纳入保活并尝试建连（不写布局/绑定）。</summary>
    public async Task EnsureAdHocPeerAsync(string peerName, CancellationToken ct = default)
    {
        var name = (peerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, LocalMachine, StringComparison.OrdinalIgnoreCase))
            return;

        _adHocPeers[name] = 0;
        if (!IsRunning)
            await StartAsync().ConfigureAwait(false);
        var token = ct;
        if (_cts != null)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            token = linked.Token;
            await EnsureOutboundAsync(name, token).ConfigureAwait(false);
            await EnsureOutboundBulkAsync(name, token).ConfigureAwait(false);
            return;
        }

        await EnsureOutboundAsync(name, token).ConfigureAwait(false);
        await EnsureOutboundBulkAsync(name, token).ConfigureAwait(false);
    }

    public void RemoveAdHocPeer(string? peerName)
    {
        var name = (peerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        _adHocPeers.TryRemove(name, out _);
    }

    public bool IsAdHocPeer(string? peerName)
    {
        var name = (peerName ?? "").Trim();
        return !string.IsNullOrWhiteSpace(name) && _adHocPeers.ContainsKey(name);
    }

    /// <summary>
    /// 远程/中转来源不可进电脑布局（布局仅局域网；远程用设备管理投影）。
    /// </summary>
    public bool IsLayoutIneligiblePeer(string? peerName)
    {
        var name = (peerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (string.Equals(name, LocalMachine, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var p in _discovery.GetPeers())
        {
            if (!string.Equals(p.MachineName, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (p.Origin is DiscoveryOrigin.Remote or DiscoveryOrigin.Service)
                return true;
            if (MatchesRemoteConfiguredEndpoint(name, p.Ip, p.ObservedIp))
                return true;
        }

        foreach (var ep in _settings.ConnectionEndpoints ?? [])
        {
            if (ep.Kind != ConnectionEndpoint.KindRemote && ep.Kind != ConnectionEndpoint.KindRelay)
                continue;
            if (!string.IsNullOrWhiteSpace(ep.VerifiedPeerName)
                && string.Equals(ep.VerifiedPeerName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool MatchesRemoteConfiguredEndpoint(string name, string? ip, string? observedIp)
    {
        foreach (var ep in _settings.ConnectionEndpoints ?? [])
        {
            if (ep.Kind != ConnectionEndpoint.KindRemote)
                continue;
            if (!string.IsNullOrWhiteSpace(ep.VerifiedPeerName)
                && string.Equals(ep.VerifiedPeerName, name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(ip)
                && !string.IsNullOrWhiteSpace(ep.Address)
                && ep.Address.Contains(ip, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(observedIp)
                && !string.IsNullOrWhiteSpace(ep.Address)
                && ep.Address.Contains(observedIp, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public bool IsPeerReachable(string name)
        => _links.TryGetValue(name, out var link) && link.ControlAlive;

    /// <summary>本机与对端 Caps 交集；对端未宣告（旧端）时等同本机 Caps。</summary>
    public string[] GetEffectiveCaps(string peerName)
    {
        if (!_links.TryGetValue(peerName, out var link))
            return LocalPeerIdentity.Caps;
        return LocalPeerIdentity.IntersectCaps(link.RemoteInfo.Caps);
    }

    public bool PeerSupports(string peerName, string cap)
        => GetEffectiveCaps(peerName)
            .Contains(cap, StringComparer.OrdinalIgnoreCase);

    /// <summary>边缘切换要求对端可达且双方均具备 EdgeMouse。</summary>
    public bool IsPeerEdgeMouseCapable(string peerName)
        => IsPeerReachable(peerName) && PeerSupports(peerName, PeerCaps.EdgeMouse);

    public PeerRemoteInfo? GetPeerRemoteInfo(string peerName)
        => _links.TryGetValue(peerName, out var link) ? link.RemoteInfo : null;

    /// <summary>查询对端出入站连接状态（诊断用）。</summary>
    public PeerConnectionStatus GetPeerStatus(string name)
    {
        var outbound = false;
        var inbound = false;
        if (_links.TryGetValue(name, out var link))
        {
            outbound = link.Outbound is { IsAlive: true };
            inbound = link.Inbound is { IsAlive: true };
        }

        return new PeerConnectionStatus(name, outbound, inbound, _discovery.FindIp(name));
    }

    public IReadOnlyList<PeerConnectionStatus> GetConfiguredPeerStatuses()
        => ConfiguredPeers().Select(GetPeerStatus).ToList();

    public bool IsListening => _listener != null;

    public async Task<bool> SendToAsync(string peerName, MessageType type, byte[]? payload = null)
    {
        if (!_links.TryGetValue(peerName, out var link))
            return false;

        // 大块消息优先走独立 bulk 连接，避免堵死键鼠控制面；无 bulk 时回退控制通道（兼容旧端）
        var useBulk = PreferBulkChannel(link, type);
        if (!useBulk && !link.ControlAlive)
            return false;

        try
        {
            if (useBulk)
                await link.SendBulkAsync(type, payload).ConfigureAwait(false);
            else
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

    /// <summary>投影帧：队列里只留最新一张，避免停投影的信令被一长串旧画面挡住。</summary>
    public bool SendLatest(string peerName, MessageType type, byte[]? payload = null)
    {
        if (!_links.TryGetValue(peerName, out var link))
            return false;

        var useBulk = PreferBulkChannel(link, type);
        if (!useBulk && !link.ControlAlive)
            return false;

        try
        {
            if (useBulk)
                link.SendLatestBulk(type, payload);
            else
                link.SendLatest(type, payload);
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
    }

    /// <summary>控制面不可用时，把同一条信令写到 bulk，接收端两条连接都会分发。</summary>
    public async Task<bool> SendOnBulkAsync(string peerName, MessageType type, byte[]? payload = null)
    {
        if (!_links.TryGetValue(peerName, out var link) || !link.BulkAlive)
            return false;

        try
        {
            await link.SendBulkAsync(type, payload).ConfigureAwait(false);
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

    /// <summary>
    /// 大帧是否走 bulk。仅有出站 bulk、无入站 bulk 时，远程投影大帧常「发得出收不到」，
    /// 此时若有控制面（尤其对端连入的入站控制）则回退控制面回写。
    /// </summary>
    private static bool PreferBulkChannel(PeerLink link, MessageType type)
    {
        if (!BulkMessageTypes.IsBulk(type) || !link.BulkAlive)
            return false;
        if (link.InboundBulk is { IsAlive: true })
            return true;
        return !link.ControlAlive;
    }

    /// <summary>
    /// 对端是否显式宣告 BulkTransfer（旧端 Caps 为空时 Intersect 会误判为本机全能力，故不可用 PeerSupports）。
    /// </summary>
    private static bool RemoteAdvertisesBulk(PeerRemoteInfo info)
        => info.Caps.Length > 0
           && info.Caps.Contains(PeerCaps.BulkTransfer, StringComparer.OrdinalIgnoreCase);

    public async Task BroadcastAsync(MessageType type, byte[]? payload = null, string? requiredCap = null)
    {
        foreach (var name in _links.Keys.ToArray())
        {
            if (requiredCap != null && !PeerSupports(name, requiredCap))
                continue;
            await SendToAsync(name, type, payload).ConfigureAwait(false);
        }
    }

    /// <summary>向已连接对端广播本机最新 Platform/DeviceClass/Caps（工具安装后无需重连）。</summary>
    public Task BroadcastLocalPeerInfoAsync()
    {
        var payload = LocalPeerIdentity.Enrich(new HandshakePayload
        {
            MachineName = LocalMachine,
            LocalIp = AdvertisedLocalIp,
            IsReply = false
        });
        // 不带密钥：仅身份/能力刷新
        payload.SecurityKey = "";
        return BroadcastAsync(MessageType.PeerInfo, MessageCodec.JsonBytes(payload));
    }

    /// <summary>随后约 45 秒内，连向该电脑的握手会请求对方把本机加入布局。</summary>
    public void ArmLayoutJoin(string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName)) return;
        _layoutJoinUntil[peerName.Trim()] = Environment.TickCount64 + 45_000;
    }

    public void ClearLayoutJoin(string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName)) return;
        _layoutJoinUntil.TryRemove(peerName.Trim(), out _);
    }

    public async Task ReconnectNowAsync()
    {
        if (_cts == null) return;
        foreach (var peer in ConfiguredPeers())
        {
            // 刷新/重连时重新请求对端把本机加入布局（仅 45 秒，避免被移除的电脑后台一直加回）
            ArmLayoutJoin(peer);
            await EnsureOutboundAsync(peer, _cts.Token).ConfigureAwait(false);
            await EnsureOutboundBulkAsync(peer, _cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 不断开现有有效链路：剪去已不在布局中的对端，并为布局内对端补出站/bulk。
    /// 端口、网卡、密钥变更须改用 <see cref="StartAsync"/>（会先 Stop）。
    /// </summary>
    public async Task RefreshConfiguredPeersAsync()
    {
        if (!IsRunning)
        {
            await StartAsync().ConfigureAwait(false);
            return;
        }

        await PruneUnconfiguredLinksAsync().ConfigureAwait(false);
        await ReconnectNowAsync().ConfigureAwait(false);
    }

    /// <summary>把连接配置中的远程/中转地址同步为发现周期/突发的单播探测目标（带来源标记）。</summary>
    public void RefreshDiscoveryProbeHosts()
    {
        _settings.EnsureCollections();
        var tcpPort = _settings.TcpPort > 0 ? _settings.TcpPort : 15155;
        var targets = new List<(string Host, int Origin)>();
        foreach (var ep in _settings.ConnectionEndpoints ?? [])
        {
            if (string.IsNullOrWhiteSpace(ep.Address)) continue;
            var raw = ep.Address.Trim();
            // wss/ws 中转：无 UDP host 可探，跳过（靠后续客户端 WS 发现）
            if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ConnectionEndpointProbe.TryParseHostPort(raw, out var host, out _, tcpPort))
                continue;

            var origin = ep.Kind == ConnectionEndpoint.KindRelay
                ? DiscoveryOrigin.Service
                : DiscoveryOrigin.Remote;
            targets.Add((host, origin));
        }

        _discovery.SetProbeTargets(targets);
    }

    /// <summary>
    /// 设备管理「刷新发现」：全网卡（含 Tailscale）突发 UDP 广播，
    /// 并对连接配置中的远程/中转地址单播宣告 + 短握手学入发现表。
    /// 发现来源写入 Origin：局域网=本地，远程配置=远程，中转=服务。
    /// </summary>
    public async Task RefreshDiscoveryProbeAsync(CancellationToken ct = default)
    {
        _settings.EnsureCollections();
        var tcpPort = _settings.TcpPort > 0 ? _settings.TcpPort : 15155;
        RefreshDiscoveryProbeHosts();
        var hostList = new List<string>();
        foreach (var ep in _settings.ConnectionEndpoints ?? [])
        {
            if (string.IsNullOrWhiteSpace(ep.Address)) continue;
            var raw = ep.Address.Trim();
            if (raw.Contains("://", StringComparison.Ordinal)) continue;
            if (!ConnectionEndpointProbe.TryParseHostPort(raw, out var host, out _, tcpPort))
                continue;
            hostList.Add(host);
        }

        if (_discovery.UdpPort <= 0)
            _discovery.AnnounceTcpPort = tcpPort;

        await _discovery.BurstProbeAsync(hostList, includeOverlayNics: true, ct).ConfigureAwait(false);

        var announceIp = AdvertisedLocalIp;
        var key = _settings.SecurityKey ?? "";
        // Tailscale/跨网：与出站探测一致给足超时
        var timeout = TimeSpan.FromSeconds(15);
        foreach (var ep in _settings.ConnectionEndpoints ?? [])
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(ep.Address)) continue;
            var raw = ep.Address.Trim();
            if (raw.Contains("://", StringComparison.Ordinal)) continue;
            if (!ConnectionEndpointProbe.TryParseHostPort(raw, out var host, out var port, tcpPort))
                continue;

            var result = await DiscoveryIdentityProbe.ProbeAsync(
                    host, port, announceIp, key, timeout, ct)
                .ConfigureAwait(false);
            if (!result.Ok) continue;

            var origin = ep.Kind == ConnectionEndpoint.KindRelay
                ? DiscoveryOrigin.Service
                : DiscoveryOrigin.Remote;
            // 中继 ProbeOnly 回报服务名：整机记为服务入口；同地址 UDP 多设备宣告仍靠 Origin=service 包
            if (!string.IsNullOrWhiteSpace(result.Platform)
                && result.Platform.Equals("relay", StringComparison.OrdinalIgnoreCase))
                origin = DiscoveryOrigin.Service;

            DiscoveryIdentityProbe.Remember(_discovery, result, host, port, origin);
            if (!string.IsNullOrWhiteSpace(result.MachineName))
                ep.VerifiedPeerName = result.MachineName!;
            if (result.RttMs is int ms)
                ep.LastRttMs = ms;
            ep.State = ConnectionEndpoint.StateConnected;
            ep.LastError = "";
        }

        try { _settings.Save(); } catch { /* ignore */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var listener = _listener;
            if (listener == null) break;

            try
            {
                // 不传 CancellationToken：停止时靠 listener.Stop() 唤醒，避免 Accept 抛 OCE 刷调试器
                var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    try { client.Dispose(); } catch { /* ignore */ }
                    break;
                }

                var session = new PeerSession(client, isOutbound: false);
                WireSession(session);
                // 等待对端发 Handshake；超时丢弃（不用 ct，避免取消异常）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(5000).ConfigureAwait(false);
                        if (!session.Authenticated)
                            await session.DisposeAsync().ConfigureAwait(false);
                    }
                    catch { /* ignore */ }
                });
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested || _listener == null)
            {
                break;
            }
            catch (InvalidOperationException) when (_listener == null)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested || _listener == null) break;
                try { await Task.Delay(200).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    private async Task MaintainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PruneUnconfiguredLinksAsync().ConfigureAwait(false);

                foreach (var peer in ConfiguredPeers())
                {
                    await EnsureOutboundAsync(peer, ct).ConfigureAwait(false);
                    await EnsureOutboundBulkAsync(peer, ct).ConfigureAwait(false);
                }

                foreach (var kv in _links.ToArray())
                    await kv.Value.HeartbeatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore */ }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>断开并移除已不在布局中的对端链路。</summary>
    public async Task DropPeerAsync(string peerName)
    {
        if (string.IsNullOrWhiteSpace(peerName)) return;
        ClearLayoutJoin(peerName);
        if (!_links.TryRemove(peerName, out var link)) return;
        try { await link.DisposeAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
        PeerLinkChanged?.Invoke(peerName, false, false);
    }

    /// <summary>清理已不在布局中的对端链路。</summary>
    public async Task PruneUnconfiguredLinksAsync()
    {
        var allowed = new HashSet<string>(ConfiguredPeers(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in _links.Keys.ToArray())
        {
            if (allowed.Contains(name)) continue;
            await DropPeerAsync(name).ConfigureAwait(false);
        }
    }

    private async Task EnsureOutboundAsync(string peerName, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var link = _links.GetOrAdd(peerName, _ => new PeerLink(peerName));
        if (link.Outbound is { IsAlive: true, Authenticated: true })
            return;
        if (!_outboundBusy.TryAdd(peerName, 0))
            return;

        try
        {
            List<(string Ip, int Port)> targets;
            try
            {
                targets = await GetConnectTargetsAsync(peerName, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (targets.Count == 0)
            {
                ReportConnectFailed(peerName, "局域网未发现地址");
                await AskPeerToDialAsync().ConfigureAwait(false);
                return;
            }

            var nic = _settings.SameSubnetOnly
                ? NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp)
                : null;
            var tried = new List<string>();
            foreach (var (ip, port) in targets)
            {
                if (ct.IsCancellationRequested) return;
                if (nic != null && !NicService.SameSubnet(nic.IPv4, nic.SubnetMask, ip))
                    continue;
                tried.Add($"{ip}:{port}");
                if (await TryOutboundHandshakeAsync(link, peerName, ip, port, bulk: false, ct).ConfigureAwait(false))
                    return;
            }

            if (tried.Count == 0)
                return;
            ReportConnectFailed(peerName, string.Join("、", tried));
            await AskPeerToDialAsync().ConfigureAwait(false);
        }
        finally
        {
            _outboundBusy.TryRemove(peerName, out _);
        }
    }

    /// <summary>
    /// 在控制面已连通且对端宣告 BulkTransfer 时，建立第二条 TCP（对端宣告端口）专传文件/剪贴板分块。
    /// </summary>
    private async Task EnsureOutboundBulkAsync(string peerName, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        if (!_links.TryGetValue(peerName, out var link) || !link.ControlAlive)
            return;
        if (!RemoteAdvertisesBulk(link.RemoteInfo))
            return;
        if (link.OutboundBulk is { IsAlive: true, Authenticated: true })
            return;
        if (!_bulkBusy.TryAdd(peerName, 0))
            return;

        try
        {
            List<(string Ip, int Port)> targets;
            try
            {
                targets = await GetConnectTargetsAsync(peerName, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var nic = _settings.SameSubnetOnly
                ? NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp)
                : null;
            foreach (var (ip, port) in targets)
            {
                if (ct.IsCancellationRequested) return;
                if (nic != null && !NicService.SameSubnet(nic.IPv4, nic.SubnetMask, ip))
                    continue;
                if (await TryOutboundHandshakeAsync(link, peerName, ip, port, bulk: true, ct).ConfigureAwait(false))
                    return;
            }
        }
        finally
        {
            _bulkBusy.TryRemove(peerName, out _);
        }
    }

    /// <summary>
    /// 连到一个候选地址并完成握手。先挂上应答等待再发送，避免局域网回包快于订阅而丢掉 HandshakeAck。
    /// </summary>
    private async Task<bool> TryOutboundHandshakeAsync(
        PeerLink link, string peerName, string ip, int port, bool bulk, CancellationToken ct)
    {
        TcpClient? client = null;
        PeerSession? session = null;
        try
        {
            client = new TcpClient();
            BindOutbound(client, ip);

            var connectTimeout = OutboundConnectTimeout(ip);
            if (!await ConnectWithTimeoutAsync(client, IPAddress.Parse(ip), port, connectTimeout, ct)
                    .ConfigureAwait(false))
            {
                client.Dispose();
                client = null;
                return false;
            }

            session = new PeerSession(client, isOutbound: true);
            client = null;
            WireSession(session);

            var layoutJoin = !bulk && IsLayoutJoinArmed(peerName);
            // 控制面与 bulk 均可带 AdHoc：对端不在布局时靠此建临时链（投影）
            var adHoc = !layoutJoin && IsAdHocPeer(peerName);
            var hs = MessageCodec.EncodeJson(MessageType.Handshake, LocalPeerIdentity.Enrich(new HandshakePayload
            {
                MachineName = LocalMachine,
                SecurityKey = _settings.SecurityKey,
                LocalIp = AdvertisedLocalIp,
                Channel = bulk ? PeerChannel.Bulk : PeerChannel.Control,
                RequestLayoutJoin = layoutJoin,
                AdHocConnect = adHoc
            }));
            // Tailscale/VPN 握手回包也可能慢于局域网的 3s
            var ackTimeout = connectTimeout > TimeSpan.FromSeconds(5)
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(3);
            var ackWait = WaitMessageAsync(session, MessageType.HandshakeAck, ackTimeout, ct);
            await session.SendRawAsync(hs).ConfigureAwait(false);
            var ackMsg = await ackWait.ConfigureAwait(false);
            if (ackMsg == null)
            {
                UnwireSession(session);
                await session.DisposeAsync().ConfigureAwait(false);
                session = null;
                return false;
            }

            var ack = MessageCodec.DecodeJson<HandshakePayload>(ackMsg.Value.Payload);
            if (!TryAcceptAck(peerName, ack, out var failStatus))
            {
                if (failStatus != null)
                    StatusMessage?.Invoke(failStatus);
                UnwireSession(session);
                await session.DisposeAsync().ConfigureAwait(false);
                session = null;
                return false;
            }

            var remote = PeerRemoteInfo.FromHandshake(ack!);
            if (bulk)
            {
                session.SetPeerIdentity(peerName, remote, isBulkChannel: true);
                await link.SetOutboundBulkAsync(session).ConfigureAwait(false);
                session = null;
                StatusMessage?.Invoke($"已建立文件传输通道 → {peerName}");
                return true;
            }

            ClearLayoutJoin(peerName);
            session.SetPeerIdentity(peerName, remote);
            await link.SetOutboundAsync(session).ConfigureAwait(false);
            link.UpdateRemoteInfo(remote);
            RememberPeerAddress(peerName, ip, ack!.LocalIp);
            session = null;
            NotifyLink(peerName, link);
            StatusMessage?.Invoke($"已出站连接 {peerName} ({ip}) · {remote}");
            if (RemoteAdvertisesBulk(remote))
                _ = EnsureOutboundBulkAsync(peerName, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (session != null)
            {
                try
                {
                    UnwireSession(session);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
            else
            {
                try { client?.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private void BindOutbound(TcpClient client, string? targetIp = null)
    {
        // Android / 自动监听：交给系统选路（否则绑死 LAN 后连 Tailscale 100.x 必挂）。
        if (_bindIp == "0.0.0.0" || OperatingSystem.IsAndroid())
            return;
        // 目标不在偏好网卡网段时也不强绑（跨网段 VPN / 双栈）
        if (!string.IsNullOrWhiteSpace(targetIp))
        {
            var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
            var mask = nic?.SubnetMask;
            if (string.IsNullOrWhiteSpace(mask) || !NicService.SameSubnet(_bindIp, mask, targetIp))
                return;
        }

        client.Client.Bind(new IPEndPoint(IPAddress.Parse(_bindIp), 0));
    }

    /// <summary>同网段 3s；Tailscale/VPN/跨网段与探针一致 15s。</summary>
    private TimeSpan OutboundConnectTimeout(string targetIp)
    {
        var nic = NicService.ResolvePreferred(_settings.PreferredNicId, _settings.PreferredLocalIp);
        if (nic != null
            && !NicService.IsAutomatic(nic)
            && !string.IsNullOrWhiteSpace(nic.IPv4)
            && nic.IPv4 != "0.0.0.0"
            && NicService.SameSubnet(nic.IPv4, nic.SubnetMask, targetIp))
            return TimeSpan.FromSeconds(3);
        return TimeSpan.FromSeconds(15);
    }

    private async Task<List<(string Ip, int Port)>> GetConnectTargetsAsync(string peerName, CancellationToken ct)
    {
        var list = new List<(string Ip, int Port)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultPort = _settings.TcpPort > 0 ? _settings.TcpPort : 15155;

        void Add(string? ip, int port)
        {
            if (string.IsNullOrWhiteSpace(ip) || port <= 0) return;
            var key = $"{ip}:{port}";
            if (!seen.Add(key)) return;
            list.Add((ip, port));
        }

        async Task AddHostAsync(string? hostOrIp, int port)
        {
            if (string.IsNullOrWhiteSpace(hostOrIp) || port <= 0) return;
            if (IPAddress.TryParse(hostOrIp, out _))
            {
                Add(hostOrIp, port);
                return;
            }

            // 配置里写的是主机名时才解析；解析失败不阻塞其它候选
            var resolved = await NicService.ResolveHostAsync(hostOrIp, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolved))
                Add(resolved, port);
        }

        // 用户验证过的远程地址优先（Tailscale 等）；发现到的局域网 IP 可能 SYN 通不了仍要回退
        foreach (var t in EnumerateConfiguredEndpointTargets(peerName))
            await AddHostAsync(t.Host, t.Port).ConfigureAwait(false);

        if (_discovery.TryGetConnectTargets(peerName, defaultPort, out var found))
        {
            foreach (var t in found)
                Add(t.Ip, t.Port);
        }

        // 发现表 FindIp 兜底（与 TryGetConnectTargets 同源，防止漏收）
        var discIp = _discovery.FindIp(peerName);
        if (!string.IsNullOrWhiteSpace(discIp))
            Add(discIp, defaultPort);

        if (_links.TryGetValue(peerName, out var link))
        {
            foreach (var known in new[]
                     {
                         link.Outbound?.PeerIp, link.Inbound?.PeerIp,
                         link.OutboundBulk?.PeerIp, link.InboundBulk?.PeerIp
                     })
            {
                if (DiscoveryService.IsUsableLanIpv4(known))
                    Add(known, defaultPort);
            }
        }

        // 切勿把 Windows 计算机名拿去 DNS（会「不知道这样的主机」刷屏）；仅 FQDN 才回退解析
        if (list.Count == 0 && LooksLikeDnsHost(peerName))
        {
            var dns = await NicService.ResolveHostAsync(peerName, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dns))
                Add(dns, defaultPort);
        }

        return list;
    }

    /// <summary>含点且非纯 IP → 才可能是 DNS 主机名；NetBIOS 计算机名不含点。</summary>
    private static bool LooksLikeDnsHost(string? host)
        => !string.IsNullOrWhiteSpace(host)
           && host.Contains('.')
           && !IPAddress.TryParse(host, out _);

    private IEnumerable<(string Host, int Port)> EnumerateConfiguredEndpointTargets(string peerName)
    {
        foreach (var b in _settings.RemoteBindings ?? [])
        {
            if (!string.Equals(b.RemoteName, peerName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryParseConnectEndpoint(b.Endpoint, out var host, out var port))
                yield return (host, port);

            // 绑定 Endpoint 为空时，仍用 EndpointId 找回已测通的连接配置地址
            if (string.IsNullOrWhiteSpace(b.EndpointId))
                continue;
            var byId = (_settings.ConnectionEndpoints ?? [])
                .FirstOrDefault(e => string.Equals(e.Id, b.EndpointId, StringComparison.OrdinalIgnoreCase));
            if (byId != null && TryParseConnectEndpoint(byId.Address, out host, out port))
                yield return (host, port);
        }

        foreach (var ep in _settings.ConnectionEndpoints ?? [])
        {
            if (ep.Kind != ConnectionEndpoint.KindRemote)
                continue;
            if (!string.Equals(ep.VerifiedPeerName, peerName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryParseConnectEndpoint(ep.Address, out var host, out var port))
                yield return (host, port);
        }
    }

    private bool TryParseConnectEndpoint(string? raw, out string host, out int port)
    {
        host = "";
        port = _settings.TcpPort > 0 ? _settings.TcpPort : 15155;
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() is "—" or "-")
            return false;
        if (!ConnectionEndpointProbe.TryParseHostPort(raw.Trim(), out host, out port, port))
            return false;
        // Tailscale 100.x / 公网等：不能只用 IsUsableLanIpv4
        return IPAddress.TryParse(host, out _) || !string.IsNullOrWhiteSpace(host);
    }

    private bool TryAcceptAck(string peerName, HandshakePayload? ack, out string? failStatus)
    {
        failStatus = null;
        if (ack == null)
        {
            failStatus = $"与 {peerName} 握手无响应";
            return false;
        }

        if (ack.RejectReason == "full")
        {
            failStatus = $"{peerName} 的电脑布局已满，无法自动添加";
            return false;
        }

        if (ack.RejectReason == "layout")
        {
            failStatus = $"{peerName} 的布局中没有本机，连接被拒绝";
            return false;
        }

        if (ack.RejectReason == "key" || !ValidateKey(ack.SecurityKey))
        {
            ReportKeyMismatch(peerName);
            return false;
        }

        return true;
    }

    private void OnPeerDialRequested(string name, string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ip))
            return;
        if (!ConfiguredPeers().Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        var ct = _cts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested)
            return;
        _ = EnsureOutboundAsync(name, ct);
    }

    private async Task AskPeerToDialAsync()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastDialTick) < 8000)
            return;
        Interlocked.Exchange(ref _lastDialTick, now);
        try
        {
            await _discovery.RequestDialAsync(AdvertisedLocalIp, _settings.TcpPort).ConfigureAwait(false);
        }
        catch
        {
            // 发现口未就绪
        }
    }

    private void ReportConnectFailed(string peerName, string detail)
    {
        var now = Environment.TickCount64;
        if (_connectFailNotice.TryGetValue(peerName, out var last) && now - last < 8000)
            return;
        _connectFailNotice[peerName] = now;
        StatusMessage?.Invoke($"无法连接 {peerName}（{detail}）");
    }

    /// <summary>
    /// 带超时的 TCP 连接；超时或取消时关闭 socket 并返回 false，不向外抛 OCE。
    /// </summary>
    private static async Task<bool> ConnectWithTimeoutAsync(
        TcpClient client, IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var connectTask = client.ConnectAsync(address, port);
            var delayTask = Task.Delay(timeout, ct);
            var completed = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);

            if (completed != connectTask)
            {
                // 超时或外部取消：关掉 socket，让 ConnectAsync 尽快结束。
                // 不 await 失败任务，避免 HostUnreachable 等在调试器里刷一阶异常栈。
                try { client.Close(); } catch { /* ignore */ }
                ObserveConnectFault(connectTask);
                return false;
            }

            await connectTask.ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException)
        {
            try { client.Close(); } catch { /* ignore */ }
            return false;
        }
        catch (SocketException)
        {
            try { client.Close(); } catch { /* ignore */ }
            return false;
        }
        catch
        {
            try { client.Close(); } catch { /* ignore */ }
            return false;
        }
    }

    private static void ObserveConnectFault(Task connectTask)
    {
        if (connectTask.IsCompleted)
        {
            _ = connectTask.Exception;
            return;
        }

        _ = connectTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

            if (msg.Type == MessageType.PeerInfo)
            {
                ApplyPeerInfoUpdate(session.PeerName, msg);
                return;
            }

            MessageReceived?.Invoke(session.PeerName, msg);
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyPeerInfoUpdate(string peerName, NetMessage msg)
    {
        var hs = MessageCodec.DecodeJson<HandshakePayload>(msg.Payload);
        if (hs == null) return;
        if (!_links.TryGetValue(peerName, out var link)) return;

        // 保留已有字段，避免对端发残缺包把 Caps 清空
        var prev = link.RemoteInfo;
        var next = PeerRemoteInfo.FromHandshake(hs);
        if (next.Caps.Length == 0 && prev.Caps.Length > 0)
            next = new PeerRemoteInfo
            {
                ProtocolVersion = next.ProtocolVersion > 0 ? next.ProtocolVersion : prev.ProtocolVersion,
                Platform = string.IsNullOrWhiteSpace(next.Platform) ? prev.Platform : next.Platform,
                DeviceClass = string.IsNullOrWhiteSpace(next.DeviceClass) ? prev.DeviceClass : next.DeviceClass,
                Caps = prev.Caps,
                AppVersion = string.IsNullOrWhiteSpace(next.AppVersion) ? prev.AppVersion : next.AppVersion
            };
        else if (string.IsNullOrWhiteSpace(next.Platform) || string.IsNullOrWhiteSpace(next.DeviceClass))
            next = new PeerRemoteInfo
            {
                ProtocolVersion = next.ProtocolVersion > 0 ? next.ProtocolVersion : prev.ProtocolVersion,
                Platform = string.IsNullOrWhiteSpace(next.Platform) ? prev.Platform : next.Platform,
                DeviceClass = string.IsNullOrWhiteSpace(next.DeviceClass) ? prev.DeviceClass : next.DeviceClass,
                Caps = next.Caps.Length > 0 ? next.Caps : prev.Caps,
                AppVersion = string.IsNullOrWhiteSpace(next.AppVersion) ? prev.AppVersion : next.AppVersion
            };

        link.UpdateRemoteInfo(next);
        try { PeerInfoUpdated?.Invoke(peerName); } catch { /* ignore */ }
    }

    private async Task CompleteInboundHandshakeAsync(PeerSession session, NetMessage msg)
    {
        if (session.Authenticated) return;

        var hs = MessageCodec.DecodeJson<HandshakePayload>(msg.Payload);
        if (hs == null || string.IsNullOrWhiteSpace(hs.MachineName))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (!ValidateKey(hs.SecurityKey))
        {
            ReportKeyMismatch(hs.MachineName);
            await SendHandshakeRejectAsync(session, "key").ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // 刷新发现 / 连接配置探测：只回身份，不建链、不占布局
        if (hs.ProbeOnly && hs.Channel != PeerChannel.Bulk)
        {
            var probeAck = MessageCodec.EncodeJson(MessageType.HandshakeAck, LocalPeerIdentity.Enrich(new HandshakePayload
            {
                MachineName = LocalMachine,
                SecurityKey = _settings.SecurityKey,
                LocalIp = AdvertisedLocalIp,
                Channel = PeerChannel.Control,
                IsReply = true,
                ProbeOnly = true
            }));
            await session.SendRawAndWaitAsync(probeAck).ConfigureAwait(false);
            RememberPeerAddress(hs.MachineName, session.PeerIp, hs.LocalIp);
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

        var isBulk = hs.Channel == PeerChannel.Bulk;

        // 布局成员 / 临时投影保活 / 布局加入请求；禁止无关主机入站占槽
        // bulk 不得触发布局加入；临时投影不写布局
        var configured = ConfiguredPeers().Contains(hs.MachineName, StringComparer.OrdinalIgnoreCase);
        if (!configured && hs.AdHocConnect)
        {
            _adHocPeers[hs.MachineName.Trim()] = 0;
            configured = true;
            if (!isBulk)
                StatusMessage?.Invoke($"已接受临时连接 ← {hs.MachineName}（投影/直连，不进布局）");
        }
        if (!configured && !isBulk && hs.RequestLayoutJoin && TryAcceptLayoutJoin != null)
        {
            if (TryAcceptLayoutJoin(hs.MachineName))
                configured = true;
            else
            {
                await SendHandshakeRejectAsync(session, "full").ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
        if (!configured)
        {
            StatusMessage?.Invoke($"未授权主机尝试连接: {hs.MachineName}");
            await SendHandshakeRejectAsync(session, "layout").ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // bulk 入站：本机也须具备 BulkTransfer（否则对端不应发 Channel=bulk）
        if (isBulk && !LocalPeerIdentity.Caps.Contains(PeerCaps.BulkTransfer, StringComparer.OrdinalIgnoreCase))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var ack = MessageCodec.EncodeJson(MessageType.HandshakeAck, LocalPeerIdentity.Enrich(new HandshakePayload
        {
            MachineName = LocalMachine,
            SecurityKey = _settings.SecurityKey,
            LocalIp = AdvertisedLocalIp,
            Channel = isBulk ? PeerChannel.Bulk : PeerChannel.Control,
            IsReply = true
        }));
        await session.SendRawAsync(ack).ConfigureAwait(false);
        var remote = PeerRemoteInfo.FromHandshake(hs);
        session.SetPeerIdentity(hs.MachineName, remote, isBulkChannel: isBulk);

        var link = _links.GetOrAdd(hs.MachineName, _ => new PeerLink(hs.MachineName));
        if (isBulk)
        {
            await link.SetInboundBulkAsync(session).ConfigureAwait(false);
            RememberPeerAddress(hs.MachineName, session.PeerIp, hs.LocalIp);
            StatusMessage?.Invoke($"已接受文件传输通道 ← {hs.MachineName}");
        }
        else
        {
            await link.SetInboundAsync(session).ConfigureAwait(false);
            link.UpdateRemoteInfo(remote);
            RememberPeerAddress(hs.MachineName, session.PeerIp, hs.LocalIp);
            NotifyLink(hs.MachineName, link);
            StatusMessage?.Invoke(string.Format(
                Helpers.Localization.Loc.T("Status.Inbound"), hs.MachineName) + $" · {remote}");

            if (RemoteAdvertisesBulk(remote))
                _ = EnsureOutboundBulkAsync(hs.MachineName, _cts?.Token ?? CancellationToken.None);
        }
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

    private bool IsLayoutJoinArmed(string peerName)
    {
        if (!_layoutJoinUntil.TryGetValue(peerName, out var until)) return false;
        if (Environment.TickCount64 <= until) return true;
        _layoutJoinUntil.TryRemove(peerName, out _);
        return false;
    }

    private void ReportKeyMismatch(string peerName)
    {
        var now = Environment.TickCount64;
        if (_keyMismatchNotice.TryGetValue(peerName, out var last) && now - last < 8000)
            return;
        _keyMismatchNotice[peerName] = now;
        StatusMessage?.Invoke($"与 {peerName} 的安全密钥不一致，无法连接");
    }

    private static async Task SendHandshakeRejectAsync(PeerSession session, string reason)
    {
        var reject = MessageCodec.EncodeJson(MessageType.HandshakeAck, new HandshakePayload
        {
            MachineName = LocalPeerIdentity.MachineName,
            RejectReason = reason,
            IsReply = true
        });
        try { await session.SendRawAndWaitAsync(reject).ConfigureAwait(false); }
        catch { /* 对端已断开 */ }
    }

    private bool ValidateKey(string? key)
        => !string.IsNullOrEmpty(key) &&
           string.Equals(key.Trim(), _settings.SecurityKey.Trim(), StringComparison.Ordinal);

    private void RememberPeerAddress(string peerName, string? socketIp, string? advertisedIp)
    {
        var observed = DiscoveryService.IsUsableLanIpv4(socketIp) ? socketIp : null;
        var advertised = DiscoveryService.IsUsableLanIpv4(advertisedIp) ? advertisedIp : observed;
        if (string.IsNullOrWhiteSpace(advertised)) return;
        var origin = NicService.IsTailscaleCgNat(advertised)
                     || NicService.IsTailscaleCgNat(observed)
                     || (!NicService.IsOnLocalPhysicalSubnet(advertised)
                         && !NicService.IsOnLocalPhysicalSubnet(observed))
            ? DiscoveryOrigin.Remote
            : DiscoveryOrigin.Lan;
        _discovery.RememberPeer(peerName, advertised, _settings.TcpPort, observed, origin: origin);
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
        public PeerSession? OutboundBulk { get; private set; }
        public PeerSession? InboundBulk { get; private set; }
        public PeerRemoteInfo RemoteInfo { get; private set; } = new();
        public bool AnyAlive => ControlAlive || BulkAlive;
        public bool ControlAlive => Outbound is { IsAlive: true } || Inbound is { IsAlive: true };
        public bool BulkAlive => OutboundBulk is { IsAlive: true } || InboundBulk is { IsAlive: true };

        public PeerLink(string name) => Name = name;

        public void UpdateRemoteInfo(PeerRemoteInfo info) => RemoteInfo = info;

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

        public async Task SetOutboundBulkAsync(PeerSession session)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (OutboundBulk != null && !ReferenceEquals(OutboundBulk, session))
                    await OutboundBulk.DisposeAsync().ConfigureAwait(false);
                OutboundBulk = session;
            }
            finally { _gate.Release(); }
        }

        public async Task SetInboundBulkAsync(PeerSession session)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (InboundBulk != null && !ReferenceEquals(InboundBulk, session))
                    await InboundBulk.DisposeAsync().ConfigureAwait(false);
                InboundBulk = session;
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
                if (ReferenceEquals(OutboundBulk, session)) OutboundBulk = null;
                if (ReferenceEquals(InboundBulk, session)) InboundBulk = null;
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

        public async Task SendBulkAsync(MessageType type, byte[]? payload)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            PeerSession? primary;
            PeerSession? secondary;
            try
            {
                var a = OutboundBulk is { IsAlive: true } ? OutboundBulk : null;
                var b = InboundBulk is { IsAlive: true } ? InboundBulk : null;
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
                throw new IOException("无可用文件传输连接");

            try
            {
                await primary.SendAsync(type, payload).ConfigureAwait(false);
            }
            catch (Exception) when (secondary != null)
            {
                await secondary.SendAsync(type, payload).ConfigureAwait(false);
            }
        }

        public void SendLatest(MessageType type, byte[]? payload)
            => SendLatestOn(Outbound, Inbound, type, payload);

        public void SendLatestBulk(MessageType type, byte[]? payload)
            => SendLatestOn(OutboundBulk, InboundBulk, type, payload);

        private static void SendLatestOn(PeerSession? outbound, PeerSession? inbound, MessageType type, byte[]? payload)
        {
            var a = outbound is { IsAlive: true } ? outbound : null;
            var b = inbound is { IsAlive: true } ? inbound : null;
            // 优先入站回写：远程影子投影常见「仅对端连入本机」，出站 bulk 虽 Alive 但大帧发不出
            var primary = b ?? a;
            primary?.SendLatest(type, payload);
        }

        public async Task HeartbeatAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            PeerSession?[] sessions;
            try { sessions = [Outbound, Inbound, OutboundBulk, InboundBulk]; }
            finally { _gate.Release(); }

            foreach (var s in sessions)
            {
                if (s is not { IsAlive: true, Authenticated: true }) continue;

                // bulk 传输中心跳可能排在分块之后；用收发活动时间 + 更宽超时，避免传文件时被误拆
                var idleLimitSec = s.IsBulkChannel ? 90 : 12;
                var lastActivity = s.IsBulkChannel && s.LastSendUtc > s.LastRecvUtc
                    ? s.LastSendUtc
                    : s.LastRecvUtc;
                if ((DateTime.UtcNow - lastActivity).TotalSeconds > idleLimitSec)
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
                if (OutboundBulk != null) await OutboundBulk.DisposeAsync().ConfigureAwait(false);
                if (InboundBulk != null) await InboundBulk.DisposeAsync().ConfigureAwait(false);
                Outbound = null;
                Inbound = null;
                OutboundBulk = null;
                InboundBulk = null;
            }
            finally { _gate.Release(); }
        }
    }
}
