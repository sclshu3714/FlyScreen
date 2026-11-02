using FlyScreen.Services.Network;

namespace FlyScreen.Services.ScreenExtend;

public enum ScreenExtendRole
{
    None,
    Host,
    Sink
}

/// <summary>
/// 电脑扩展屏会话：Offer/Accept/Stop + 帧转发；穿边排除查询。
/// 采集与虚拟屏由宿主层实现。
/// </summary>
public sealed class ScreenExtendService : IDisposable
{
    private readonly ConnectionManager _connections;
    private readonly object _gate = new();
    private int _frameSeq;
    private int _audioSeq;
    private int _audioPending;
    private bool _disposed;

    public ScreenExtendService(ConnectionManager connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _connections.MessageReceived += OnPeerMessage;
        _connections.PeerLinkChanged += OnPeerLinkChanged;
    }

    public ScreenExtendRole Role { get; private set; }
    public string? Peer { get; private set; }
    public Guid SessionId { get; private set; }
    public bool IsActive => Role != ScreenExtendRole.None && !string.IsNullOrEmpty(Peer);

    /// <summary>从机收到 Offer 时：返回 true 接受。默认自动接受（桌面 Caps）。</summary>
    public Func<string, ScreenExtendOfferPayload, bool>? ShouldAcceptOffer { get; set; }

    /// <summary>从机 Accept 时上报本机主屏分辨率与缩放（可选）。</summary>
    public Func<(int Width, int Height, int ScalePercent)>? GetSinkDisplayInfo { get; set; }

    /// <summary>兼容旧回调：仅宽高。</summary>
    public Func<(int Width, int Height)>? GetSinkDisplaySize { get; set; }

    /// <summary>主机侧：对端 Accept 上报的主屏分辨率（用于对齐虚拟副屏）。</summary>
    public int PeerSinkWidth { get; private set; }
    public int PeerSinkHeight { get; private set; }
    public int PeerSinkScalePercent { get; private set; }

    /// <summary>本会话是否把主机声音送到从机，并让主机不再本地播放。</summary>
    public bool AudioSync { get; private set; }

    /// <summary>本会话是否允许从机播放器键鼠回控主机采集区。</summary>
    public bool RemoteControl { get; private set; }

    /// <summary>主机侧：对端已 Accept，应创建第二输出并开始推流。</summary>
    public event Action? HostSessionReady;

    /// <summary>从机侧：会话已建立，应打开全屏窗。</summary>
    public event Action? SinkSessionReady;

    /// <summary>会话结束（任一方 / 断连）。</summary>
    public event Action? SessionEnded;

    /// <summary>从机收到一帧 JPEG。</summary>
    public event Action<ReadOnlyMemory<byte>, int, int>? FrameReceived;

    /// <summary>从机收到一块 PCM s16le。</summary>
    public event Action<byte[], int, int>? AudioReceived;

    /// <summary>主机收到从机播放器回控输入（已校验会话）。</summary>
    public event Action<ScreenExtendInputPayload>? ReverseInputReceived;

    public event Action<string>? StatusMessage;

    /// <summary>需要弹窗/打断用户的投影提示（冲突、对端拒绝等）。</summary>
    public event Action<string>? UserNotice;

    /// <summary>穿边拓扑：该对端名是否应被排除（主机排除从机；从机排除所有穿边目标）。</summary>
    public bool IsExcludedFromEdgeCross(string? peerName)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(peerName)) return false;
        if (Role == ScreenExtendRole.Sink)
            return true; // 从机本机会结合 EasyMouse 暂停；此处对「切入 peerName」一律否
        return string.Equals(peerName, Peer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否应暂停本机 EasyMouse：从机收流时；主机开启回控时（避免与从机播放器键鼠打架）。
    /// </summary>
    public bool ShouldPauseLocalEasyMouse
        => IsActive && (Role == ScreenExtendRole.Sink
                        || (Role == ScreenExtendRole.Host && RemoteControl));

    /// <summary>从机播放器是否应采集并回传键鼠。</summary>
    public bool CanSendReverseInput
        => IsActive && Role == ScreenExtendRole.Sink && RemoteControl
           && !string.IsNullOrEmpty(Peer);

    public Task SendReverseMoveAsync(double nx, double ny)
    {
        if (!TryBeginReverse(out var peer, out var sid)) return Task.CompletedTask;
        return ScreenExtendReverseInput.SendMoveAsync(_connections, peer, sid, nx, ny);
    }

    public Task SendReverseButtonAsync(double nx, double ny, int button, bool down)
    {
        if (!TryBeginReverse(out var peer, out var sid)) return Task.CompletedTask;
        return ScreenExtendReverseInput.SendButtonAsync(_connections, peer, sid, nx, ny, button, down);
    }

    public Task SendReverseWheelAsync(double nx, double ny, int delta, bool horizontal)
    {
        if (!TryBeginReverse(out var peer, out var sid)) return Task.CompletedTask;
        return ScreenExtendReverseInput.SendWheelAsync(_connections, peer, sid, nx, ny, delta, horizontal);
    }

    public Task SendReverseKeyAsync(ushort virtualKey, bool down, bool extended = false, string? text = null)
    {
        if (!TryBeginReverse(out var peer, out var sid)) return Task.CompletedTask;
        return ScreenExtendReverseInput.SendKeyAsync(_connections, peer, sid, virtualKey, down, extended, text);
    }

    private bool TryBeginReverse(out string peer, out Guid sid)
    {
        lock (_gate)
        {
            if (!CanSendReverseInput)
            {
                peer = "";
                sid = Guid.Empty;
                return false;
            }

            peer = Peer!;
            sid = SessionId;
            return true;
        }
    }

    public async Task<(bool Ok, string? Error)> StartHostAsync(string peer,
        int preferredWidth = 1920, int preferredHeight = 1080, int fps = 12,
        bool audioSync = false,
        bool remoteControl = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(peer))
            return (false, "未指定对端");
        lock (_gate)
        {
            if (Role != ScreenExtendRole.None)
                return (false, ScreenExtendConflict.DescribeLocalBusy(Role, Peer));
        }

        // 旧端可能无 ScreenExtendSink 能力位；双向可达的电脑/大屏仍允许发起（对端忽略未知消息即可）
        if (!_connections.PeerSupports(peer, PeerCaps.ScreenExtendSink)
            && !ScreenExtendTargets.IsEligiblePeer(_connections, peer))
            return (false, "对端不支持扩展屏从机");
        if (!_connections.IsPeerReachable(peer))
            return (false, "对端未连接");

        var sessionId = Guid.NewGuid();
        lock (_gate)
        {
            if (Role != ScreenExtendRole.None)
                return (false, ScreenExtendConflict.DescribeLocalBusy(Role, Peer));
            Role = ScreenExtendRole.Host;
            Peer = peer;
            SessionId = sessionId;
            _frameSeq = 0;
            _audioSeq = 0;
            AudioSync = audioSync;
            RemoteControl = remoteControl;
        }

        var offer = new ScreenExtendOfferPayload
        {
            SessionId = sessionId.ToString("D"),
            PreferredWidth = preferredWidth,
            PreferredHeight = preferredHeight,
            Fps = fps,
            AudioSync = audioSync,
            RemoteControl = remoteControl
        };

        try
        {
            await _connections.SendToAsync(peer, MessageType.ScreenExtendOffer,
                MessageCodec.JsonBytes(offer)).ConfigureAwait(false);
            Publish($"扩展屏：已向 {peer} 发起");
            return (true, null);
        }
        catch (Exception ex)
        {
            ClearSession();
            return (false, ex.Message);
        }
    }

    public async Task StopAsync(string reason = "stopped", CancellationToken ct = default)
    {
        string? peer;
        Guid sid;
        lock (_gate)
        {
            if (!IsActive) return;
            peer = Peer;
            sid = SessionId;
        }

        if (!string.IsNullOrEmpty(peer))
        {
            try
            {
                var stop = new ScreenExtendStopPayload { SessionId = sid.ToString("D"), Reason = reason };
                var bytes = MessageCodec.JsonBytes(stop);
                // 控制面入队成功不等于对端已经读到。画面在 bulk 上，对端一定在读那条连接。
                // 停止两路都写；后到的一份会因会话已结束被忽略。这样播放器 Esc 才能让投影端停采集并恢复音量。
                await _connections.SendToAsync(peer, MessageType.ScreenExtendStop, bytes)
                    .ConfigureAwait(false);
                await _connections.SendOnBulkAsync(peer, MessageType.ScreenExtendStop, bytes)
                    .ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        EndSessionLocal("stopped");
    }

    public Task SendFrameAsync(ReadOnlyMemory<byte> jpeg, int width, int height, CancellationToken ct = default)
    {
        string? peer;
        Guid sid;
        int seq;
        lock (_gate)
        {
            if (Role != ScreenExtendRole.Host || string.IsNullOrEmpty(Peer))
                return Task.CompletedTask;
            peer = Peer;
            sid = SessionId;
            seq = ++_frameSeq;
        }

        var payload = ScreenExtendFrameCodec.Encode(sid, width, height, seq, jpeg.Span);
        try
        {
            _connections.SendLatest(peer!, MessageType.ScreenExtendFrame, payload);
        }
        catch
        {
            // 推流失败不立刻拆会话；由心跳/断连清理
        }

        return Task.CompletedTask;
    }

    /// <summary>主机把一块 PCM s16le 送给当前从机。积压过多时丢掉，避免声音把链路堵住。</summary>
    public void PostAudio(byte[] pcm, int sampleRate, int channels)
    {
        if (pcm.Length == 0 || sampleRate <= 0 || channels <= 0) return;
        if (Interlocked.Increment(ref _audioPending) > 8)
        {
            Interlocked.Decrement(ref _audioPending);
            return;
        }

        _ = SendAudioAsync(pcm, sampleRate, channels);
    }

    private async Task SendAudioAsync(byte[] pcm, int sampleRate, int channels)
    {
        try
        {
            string? peer;
            Guid sid;
            int seq;
            lock (_gate)
            {
                if (Role != ScreenExtendRole.Host || !AudioSync || string.IsNullOrEmpty(Peer))
                    return;
                peer = Peer;
                sid = SessionId;
                seq = ++_audioSeq;
            }

            var payload = ScreenExtendAudioCodec.Encode(sid, sampleRate, channels, seq, pcm);
            await _connections.SendToAsync(peer!, MessageType.ScreenExtendAudio, payload)
                .ConfigureAwait(false);
        }
        catch
        {
            // 单块声音失败不拆会话
        }
        finally
        {
            Interlocked.Decrement(ref _audioPending);
        }
    }

    private void OnPeerMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.ScreenExtendOffer:
                _ = HandleOfferAsync(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendAccept:
                HandleAccept(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendReject:
                HandleReject(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendStop:
                HandleStop(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendFrame:
                HandleFrame(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendAudio:
                HandleAudio(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendInput:
                HandleReverseInput(peer, msg.Payload);
                break;
        }
    }

    private async Task HandleOfferAsync(string peer, byte[] payload)
    {
        var offer = MessageCodec.DecodeJson<ScreenExtendOfferPayload>(payload);
        if (offer == null || !Guid.TryParse(offer.SessionId, out var sid))
            return;

        ScreenExtendRole busyRole;
        string? busyPeer;
        lock (_gate)
        {
            busyRole = Role;
            busyPeer = Peer;
        }

        if (busyRole != ScreenExtendRole.None)
        {
            var reason = ScreenExtendConflict.ReasonForRole(busyRole);
            var notice = ScreenExtendConflict.DescribeLocalRejectOffer(peer, busyRole, busyPeer);
            Publish(notice);
            NotifyUser(notice);
            await RejectAsync(peer, offer.SessionId, reason).ConfigureAwait(false);
            return;
        }

        var accept = ShouldAcceptOffer?.Invoke(peer, offer) ?? true;
        if (!accept)
        {
            await RejectAsync(peer, offer.SessionId, ScreenExtendConflict.ReasonDeclined).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            // 竞态：Accept 前又进了别的会话
            if (Role != ScreenExtendRole.None)
            {
                busyRole = Role;
                busyPeer = Peer;
            }
            else
            {
                Role = ScreenExtendRole.Sink;
                Peer = peer;
                SessionId = sid;
                _frameSeq = 0;
                _audioSeq = 0;
                AudioSync = offer.AudioSync;
                RemoteControl = offer.RemoteControl;
                busyRole = ScreenExtendRole.None;
                busyPeer = null;
            }
        }

        if (busyRole != ScreenExtendRole.None)
        {
            var reason = ScreenExtendConflict.ReasonForRole(busyRole);
            var notice = ScreenExtendConflict.DescribeLocalRejectOffer(peer, busyRole, busyPeer);
            Publish(notice);
            NotifyUser(notice);
            await RejectAsync(peer, offer.SessionId, reason).ConfigureAwait(false);
            return;
        }

        var sinkW = offer.PreferredWidth;
        var sinkH = offer.PreferredHeight;
        var sinkScale = 0;
        try
        {
            var info = GetSinkDisplayInfo?.Invoke();
            if (info is { Width: > 0, Height: > 0 })
            {
                sinkW = info.Value.Width;
                sinkH = info.Value.Height;
                sinkScale = info.Value.ScalePercent;
            }
            else
            {
                var size = GetSinkDisplaySize?.Invoke();
                if (size is { Width: > 0, Height: > 0 })
                {
                    sinkW = size.Value.Width;
                    sinkH = size.Value.Height;
                }
            }
        }
        catch { /* keep offer preferred */ }

        var acc = new ScreenExtendAcceptPayload
        {
            SessionId = offer.SessionId,
            SinkWidth = sinkW,
            SinkHeight = sinkH,
            SinkScalePercent = sinkScale
        };
        try
        {
            await _connections.SendToAsync(peer, MessageType.ScreenExtendAccept,
                MessageCodec.JsonBytes(acc)).ConfigureAwait(false);
            Publish($"投影：已接受来自 {peer}（本机 {sinkW}x{sinkH}）");
            try { SinkSessionReady?.Invoke(); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            ClearSession();
            Publish($"扩展屏接受失败：{ex.Message}");
        }
    }

    private async Task RejectAsync(string peer, string sessionId, string reason)
    {
        var rej = new ScreenExtendRejectPayload { SessionId = sessionId, Reason = reason };
        try
        {
            await _connections.SendToAsync(peer, MessageType.ScreenExtendReject,
                MessageCodec.JsonBytes(rej)).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private void HandleAccept(string peer, byte[] payload)
    {
        var acc = MessageCodec.DecodeJson<ScreenExtendAcceptPayload>(payload);
        if (acc == null || !Guid.TryParse(acc.SessionId, out var sid)) return;
        lock (_gate)
        {
            if (Role != ScreenExtendRole.Host || SessionId != sid) return;
            if (!string.Equals(Peer, peer, StringComparison.OrdinalIgnoreCase)) return;
        }

        if (acc.SinkWidth > 0 && acc.SinkHeight > 0)
        {
            PeerSinkWidth = acc.SinkWidth;
            PeerSinkHeight = acc.SinkHeight;
            if (acc.SinkScalePercent > 0)
                PeerSinkScalePercent = acc.SinkScalePercent;
        }

        Publish($"扩展屏：{peer} 已接受（从机 {PeerSinkWidth}x{PeerSinkHeight}"
                + (PeerSinkScalePercent > 0 ? $" @{PeerSinkScalePercent}%" : "")
                + "），开始推流");
        try { HostSessionReady?.Invoke(); } catch { /* ignore */ }
    }

    private void HandleReject(string peer, byte[] payload)
    {
        var rej = MessageCodec.DecodeJson<ScreenExtendRejectPayload>(payload);
        if (rej == null || !Guid.TryParse(rej.SessionId, out var sid)) return;
        lock (_gate)
        {
            if (Role != ScreenExtendRole.Host || SessionId != sid) return;
        }

        var notice = ScreenExtendConflict.DescribeRemoteReject(peer, rej.Reason);
        Publish(notice);
        NotifyUser(notice);
        EndSessionLocal("rejected");
    }

    private void NotifyUser(string message)
    {
        try { UserNotice?.Invoke(message); } catch { /* ignore */ }
    }

    private void HandleStop(string peer, byte[] payload)
    {
        var stop = MessageCodec.DecodeJson<ScreenExtendStopPayload>(payload);
        if (stop == null || !Guid.TryParse(stop.SessionId, out var sid)) return;
        lock (_gate)
        {
            if (!IsActive || SessionId != sid) return;
            if (!string.Equals(Peer, peer, StringComparison.OrdinalIgnoreCase)) return;
            // 控制面和 bulk 会各到一份。先清会话，另一份不能再进来把音量恢复做两遍。
            ResetSessionFields();
        }

        Publish($"扩展屏已结束：{stop.Reason}");
        FinishSession("remote-stop");
    }

    private void HandleFrame(string peer, byte[] payload)
    {
        if (Role != ScreenExtendRole.Sink) return;
        if (!string.Equals(Peer, peer, StringComparison.OrdinalIgnoreCase)) return;
        if (!ScreenExtendFrameCodec.TryDecode(payload, out var sid, out var w, out var h, out var seq, out var jpeg))
            return;
        if (sid != SessionId || jpeg.Length == 0) return;

        var first = false;
        lock (_gate)
        {
            if (_frameSeq == 0) first = true;
            _frameSeq = seq;
        }

        if (first)
            Publish($"投影：正在接收 {peer} 画面 {w}x{h}");

        try { FrameReceived?.Invoke(jpeg.ToArray(), w, h); } catch { /* ignore */ }
    }

    private void HandleAudio(string peer, byte[] payload)
    {
        if (Role != ScreenExtendRole.Sink || !AudioSync) return;
        if (!string.Equals(Peer, peer, StringComparison.OrdinalIgnoreCase)) return;
        if (!ScreenExtendAudioCodec.TryDecode(payload, out var sid, out var rate, out var channels, out _, out var pcm))
            return;
        if (sid != SessionId || pcm.Length == 0) return;
        try { AudioReceived?.Invoke(pcm.ToArray(), rate, channels); } catch { /* ignore */ }
    }

    private void HandleReverseInput(string peer, byte[] payload)
    {
        var input = MessageCodec.DecodeJson<ScreenExtendInputPayload>(payload);
        if (input == null || !Guid.TryParse(input.SessionId, out var sid)) return;
        lock (_gate)
        {
            if (Role != ScreenExtendRole.Host || !RemoteControl) return;
            if (SessionId != sid) return;
            if (!string.Equals(Peer, peer, StringComparison.OrdinalIgnoreCase)) return;
        }

        try { ReverseInputReceived?.Invoke(input); } catch { /* ignore */ }
    }

    private void OnPeerLinkChanged(string peer, bool outbound, bool inbound)
    {
        if (!IsActive) return;
        if (!string.Equals(Peer, peer, StringComparison.OrdinalIgnoreCase)) return;
        if (outbound || inbound) return;
        Publish($"扩展屏：对端 {peer} 断开");
        EndSessionLocal("disconnect");
    }

    private void EndSessionLocal(string reason)
    {
        ClearSession();
        FinishSession(reason);
    }

    private void FinishSession(string reason)
    {
        try { SessionEnded?.Invoke(); } catch { /* ignore */ }
        Publish($"扩展屏会话结束（{reason}）");
    }

    private void ClearSession()
    {
        lock (_gate)
            ResetSessionFields();
    }

    private void ResetSessionFields()
    {
        Role = ScreenExtendRole.None;
        Peer = null;
        SessionId = Guid.Empty;
        _frameSeq = 0;
        PeerSinkWidth = 0;
        PeerSinkHeight = 0;
        PeerSinkScalePercent = 0;
        AudioSync = false;
        RemoteControl = false;
        _audioSeq = 0;
    }

    private void Publish(string msg)
    {
        try { StatusMessage?.Invoke(msg); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 进程退出/宿主释放：尽力通知对端停投影（杀进程则靠 PeerLinkChanged）
        try
        {
            if (IsActive)
                StopAsync("dispose").GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        _connections.MessageReceived -= OnPeerMessage;
        _connections.PeerLinkChanged -= OnPeerLinkChanged;
        ClearSession();
    }
}
