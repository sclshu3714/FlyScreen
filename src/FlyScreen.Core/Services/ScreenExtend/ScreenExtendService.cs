using System.Collections.Concurrent;
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
/// Host 单路；Sink 可多路并发（播放器多 Tab）。
/// </summary>
public sealed class ScreenExtendService : IDisposable
{
    private sealed class SinkSession
    {
        public required string Peer;
        public Guid SessionId;
        public bool AudioSync;
        public bool RemoteControl;
        public int FrameSeq;
    }

    private readonly ConnectionManager _connections;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ScreenExtendHostRequestResultPayload>>
        _hostRequestWait = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SinkSession> _sinks = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSinkPeer;
    private string? _hostPeer;
    private Guid _hostSessionId;
    private bool _hostAudioSync;
    private bool _hostRemoteControl;
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

    /// <summary>Host 优先，否则为当前激活 Sink。</summary>
    public ScreenExtendRole Role { get; private set; }
    public string? Peer { get; private set; }
    public Guid SessionId { get; private set; }
    public bool IsActive => Role != ScreenExtendRole.None && !string.IsNullOrEmpty(Peer);

    public Func<string, ScreenExtendOfferPayload, bool>? ShouldAcceptOffer { get; set; }
    public Func<(int Width, int Height, int ScalePercent)>? GetSinkDisplayInfo { get; set; }
    public Func<(int Width, int Height)>? GetSinkDisplaySize { get; set; }

    public int PeerSinkWidth { get; private set; }
    public int PeerSinkHeight { get; private set; }
    public int PeerSinkScalePercent { get; private set; }

    public bool AudioSync { get; private set; }
    public bool RemoteControl { get; private set; }

    public event Action? HostSessionReady;
    /// <summary>从机侧：某路会话已建立（peer）。</summary>
    public event Action<string>? SinkSessionReady;
    /// <summary>某路结束；peer 为空表示 Host 结束或全部清空。</summary>
    public event Action<string?>? SessionEnded;
    /// <summary>从机收到一帧（含 peer）。</summary>
    public event Action<ReadOnlyMemory<byte>, int, int, long, string>? FrameReceived;
    /// <summary>从机收到一块 PCM（含 peer）。</summary>
    public event Action<byte[], int, int, string>? AudioReceived;
    public event Action<ScreenExtendInputPayload>? ReverseInputReceived;
    public event Action<string>? StatusMessage;
    public event Action<string>? UserNotice;

    public Func<string, ScreenExtendHostRequestPayload, CancellationToken,
        Task<(bool Ok, string? Error)>>? OnHostRequestAsync { get; set; }

    public IReadOnlyList<string> GetSinkPeers()
    {
        lock (_gate)
            return _sinks.Keys.ToList();
    }

    public bool IsSinkPeer(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return false;
        lock (_gate)
            return _sinks.ContainsKey(peer);
    }

    public bool TryGetSinkSession(string peer, out bool audioSync, out bool remoteControl)
    {
        lock (_gate)
        {
            if (_sinks.TryGetValue(peer, out var s))
            {
                audioSync = s.AudioSync;
                remoteControl = s.RemoteControl;
                return true;
            }
        }

        audioSync = false;
        remoteControl = false;
        return false;
    }

    /// <summary>播放器切换激活 Tab。</summary>
    public void SetActiveSinkPeer(string? peer)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(peer) || !_sinks.ContainsKey(peer))
                return;
            _activeSinkPeer = peer;
            SyncPublic_NoLock();
        }
    }

    public void NotifyHostSessionReady()
    {
        if (Role != ScreenExtendRole.Host) return;
        try { HostSessionReady?.Invoke(); } catch { /* ignore */ }
    }

    public bool IsExcludedFromEdgeCross(string? peerName)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(peerName)) return false;
        if (Role == ScreenExtendRole.Sink)
            return true;
        return string.Equals(peerName, Peer, StringComparison.OrdinalIgnoreCase);
    }

    public bool ShouldPauseLocalEasyMouse
    {
        get
        {
            lock (_gate)
            {
                if (_sinks.Count > 0) return true;
                return _hostPeer != null && _hostRemoteControl;
            }
        }
    }

    public bool CanSendReverseInput
    {
        get
        {
            lock (_gate)
            {
                if (_hostPeer != null) return false;
                if (string.IsNullOrEmpty(_activeSinkPeer)) return false;
                return _sinks.TryGetValue(_activeSinkPeer, out var s) && s.RemoteControl;
            }
        }
    }

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
            if (_hostPeer != null
                || string.IsNullOrEmpty(_activeSinkPeer)
                || !_sinks.TryGetValue(_activeSinkPeer, out var s)
                || !s.RemoteControl)
            {
                peer = "";
                sid = Guid.Empty;
                return false;
            }

            peer = _activeSinkPeer;
            sid = s.SessionId;
            return true;
        }
    }

    public async Task<(bool Ok, string? Error)> RequestPeerBecomeHostAsync(
        string remotePeer,
        string? sinkPeer = null,
        int preferredWidth = 1920,
        int preferredHeight = 1080,
        int fps = 12,
        bool audioSync = false,
        bool remoteControl = true,
        int jpegQuality = 0,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remotePeer))
            return (false, "未指定远程电脑");
        if (!_connections.IsPeerReachable(remotePeer))
            return (false, "远程电脑未连接");

        lock (_gate)
        {
            if (_hostPeer != null)
                return (false, ScreenExtendConflict.DescribeLocalBusy(ScreenExtendRole.Host, _hostPeer));
        }

        var sink = string.IsNullOrWhiteSpace(sinkPeer)
            ? LocalPeerIdentity.MachineName
            : sinkPeer.Trim();
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ScreenExtendHostRequestResultPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _hostRequestWait[requestId] = tcs;

        TaskCompletionSource<bool>? sinkReady = null;
        Action<string>? onSinkReady = null;
        var localIsSink = string.Equals(sink, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
        if (localIsSink)
        {
            // 同对端已有从机：替换该路；其它从机保留（多 Tab）
            if (IsSinkPeer(remotePeer))
            {
                try { await StopSessionAsync(remotePeer, "reproject").ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            sinkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            onSinkReady = peer =>
            {
                if (string.Equals(peer, remotePeer, StringComparison.OrdinalIgnoreCase))
                    sinkReady.TrySetResult(true);
            };
            SinkSessionReady += onSinkReady;
        }

        try
        {
            var req = new ScreenExtendHostRequestPayload
            {
                RequestId = requestId,
                SinkPeer = sink,
                PreferredWidth = preferredWidth,
                PreferredHeight = preferredHeight,
                Fps = fps,
                JpegQuality = jpegQuality,
                AudioSync = audioSync,
                RemoteControl = remoteControl,
                Stop = false
            };
            if (!await _connections.SendToAsync(remotePeer, MessageType.ScreenExtendHostRequest,
                    MessageCodec.JsonBytes(req)).ConfigureAwait(false))
                return (false, "无法向远程发送投影请求（控制连接不可用）");

            var resultTask = tcs.Task;
            var delayTask = Task.Delay(timeout ?? TimeSpan.FromSeconds(25), CancellationToken.None);
            var completed = await Task.WhenAny(resultTask, delayTask).ConfigureAwait(false);
            if (completed != resultTask)
                return (false, "等待远程开始投影超时（请确认对端已更新并支持远程投影）");

            var result = await resultTask.ConfigureAwait(false);
            if (!result.Ok)
                return (false, string.IsNullOrWhiteSpace(result.Error) ? "远程拒绝投影" : result.Error);

            if (sinkReady != null)
            {
                if (sinkReady.Task.IsCompletedSuccessfully
                    || await WaitTaskNoCancelAsync(sinkReady.Task, TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                    return (true, null);

                return (false, "远程已应答，但本机未收到投影画面会话");
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            return (false, "等待远程开始投影超时（请确认对端已更新并支持远程投影）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _hostRequestWait.TryRemove(requestId, out _);
            if (onSinkReady != null)
                SinkSessionReady -= onSinkReady;
        }
    }

    private static async Task<bool> WaitTaskNoCancelAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompletedSuccessfully) return true;
        if (task.IsCompleted) return task.IsCompletedSuccessfully;
        var delay = Task.Delay(timeout, CancellationToken.None);
        var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
        return done == task && task.IsCompletedSuccessfully;
    }

    public async Task RequestPeerEndHostAsync(string remotePeer, string? sinkPeer = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remotePeer) || !_connections.IsPeerReachable(remotePeer))
            return;
        var req = new ScreenExtendHostRequestPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SinkPeer = string.IsNullOrWhiteSpace(sinkPeer)
                ? LocalPeerIdentity.MachineName
                : sinkPeer.Trim(),
            Stop = true
        };
        try
        {
            await _connections.SendToAsync(remotePeer, MessageType.ScreenExtendHostRequest,
                MessageCodec.JsonBytes(req)).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    public async Task<(bool Ok, string? Error)> StartHostAsync(string peer,
        int preferredWidth = 1920, int preferredHeight = 1080, int fps = 12,
        bool audioSync = false,
        bool remoteControl = false,
        CancellationToken ct = default,
        bool waitForAccept = false)
    {
        if (string.IsNullOrWhiteSpace(peer))
            return (false, "未指定对端");
        lock (_gate)
        {
            if (_hostPeer != null || _sinks.Count > 0)
                return (false, ScreenExtendConflict.DescribeLocalBusy(
                    _hostPeer != null ? ScreenExtendRole.Host : ScreenExtendRole.Sink,
                    _hostPeer ?? _activeSinkPeer));
        }

        // 看对端自身 Caps，勿用 PeerSupports/Intersect（手机无 Sink 会把电脑 Sink 交掉）
        if (!_connections.PeerAdvertises(peer, PeerCaps.ScreenExtendSink)
            && !ScreenExtendTargets.IsEligiblePeer(_connections, peer))
            return (false, "对端不支持投影接收");
        if (!_connections.IsPeerReachable(peer))
            return (false, "对端未连接");

        var sessionId = Guid.NewGuid();
        lock (_gate)
        {
            if (_hostPeer != null || _sinks.Count > 0)
                return (false, ScreenExtendConflict.DescribeLocalBusy(
                    _hostPeer != null ? ScreenExtendRole.Host : ScreenExtendRole.Sink,
                    _hostPeer ?? _activeSinkPeer));
            _hostPeer = peer;
            _hostSessionId = sessionId;
            _hostAudioSync = audioSync;
            _hostRemoteControl = remoteControl;
            _frameSeq = 0;
            _audioSeq = 0;
            SyncPublic_NoLock();
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

        TaskCompletionSource<bool>? acceptTcs = null;
        Action? onHostReady = null;
        if (waitForAccept)
        {
            acceptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            onHostReady = () => acceptTcs.TrySetResult(true);
            HostSessionReady += onHostReady;
        }

        try
        {
            await _connections.SendToAsync(peer, MessageType.ScreenExtendOffer,
                MessageCodec.JsonBytes(offer)).ConfigureAwait(false);
            Publish($"投影：已向 {peer} 发起");

            if (acceptTcs != null)
            {
                if (!await WaitTaskNoCancelAsync(acceptTcs.Task, TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                {
                    ClearHostLocal();
                    return (false, "对端未接受投影会话");
                }
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            ClearHostLocal();
            return (false, "对端未接受投影会话");
        }
        catch (Exception ex)
        {
            ClearHostLocal();
            return (false, ex.Message);
        }
        finally
        {
            if (onHostReady != null)
                HostSessionReady -= onHostReady;
        }
    }

    /// <summary>停止指定从机一路；若 peer 为空或为本机 Host 对端则停 Host。</summary>
    public async Task StopSessionAsync(string? peer, string reason = "stopped", CancellationToken ct = default)
    {
        string? targetPeer;
        Guid sid;
        var isHost = false;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(peer)
                && _hostPeer != null
                && string.Equals(_hostPeer, peer, StringComparison.OrdinalIgnoreCase))
            {
                isHost = true;
                targetPeer = _hostPeer;
                sid = _hostSessionId;
            }
            else if (!string.IsNullOrWhiteSpace(peer) && _sinks.TryGetValue(peer, out var sink))
            {
                targetPeer = sink.Peer;
                sid = sink.SessionId;
            }
            else if (_hostPeer != null && string.IsNullOrWhiteSpace(peer))
            {
                isHost = true;
                targetPeer = _hostPeer;
                sid = _hostSessionId;
            }
            else
                return;
        }

        await SendStopAsync(targetPeer!, sid, reason).ConfigureAwait(false);

        if (isHost)
            EndHostLocal(targetPeer!, reason);
        else
            EndSinkLocal(targetPeer!, reason);
    }

    /// <summary>停止全部（Host + 所有 Sink）。</summary>
    public async Task StopAsync(string reason = "stopped", CancellationToken ct = default)
    {
        List<(string Peer, Guid Sid, bool Host)> targets;
        lock (_gate)
        {
            targets = new List<(string, Guid, bool)>();
            if (_hostPeer != null)
                targets.Add((_hostPeer, _hostSessionId, true));
            foreach (var s in _sinks.Values)
                targets.Add((s.Peer, s.SessionId, false));
        }

        if (targets.Count == 0) return;

        foreach (var t in targets)
        {
            try { await SendStopAsync(t.Peer, t.Sid, reason).ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        List<string> endedPeers;
        lock (_gate)
        {
            endedPeers = _sinks.Keys.ToList();
            if (_hostPeer != null)
                endedPeers.Insert(0, _hostPeer);
            ResetAll_NoLock();
        }

        if (endedPeers.Count == 0)
        {
            FinishSession(null, reason);
            return;
        }

        foreach (var p in endedPeers)
            FinishSession(p, reason);
    }

    private async Task SendStopAsync(string peer, Guid sid, string reason)
    {
        try
        {
            var stop = new ScreenExtendStopPayload { SessionId = sid.ToString("D"), Reason = reason };
            var bytes = MessageCodec.JsonBytes(stop);
            await _connections.SendToAsync(peer, MessageType.ScreenExtendStop, bytes).ConfigureAwait(false);
            await _connections.SendOnBulkAsync(peer, MessageType.ScreenExtendStop, bytes).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    public Task SendFrameAsync(ReadOnlyMemory<byte> jpeg, int width, int height, CancellationToken ct = default)
        => SendFrameAsync(jpeg, width, height, capturedUnixMs: 0, ct);

    public Task SendFrameAsync(ReadOnlyMemory<byte> jpeg, int width, int height, long capturedUnixMs,
        CancellationToken ct = default)
    {
        string? peer;
        Guid sid;
        int seq;
        lock (_gate)
        {
            if (_hostPeer == null) return Task.CompletedTask;
            peer = _hostPeer;
            sid = _hostSessionId;
            seq = ++_frameSeq;
        }

        var payload = ScreenExtendFrameCodec.Encode(sid, width, height, seq, jpeg.Span, capturedUnixMs);
        try
        {
            if (!_connections.SendLatest(peer!, MessageType.ScreenExtendFrame, payload)
                && seq <= 3)
                Publish($"投影：向 {peer} 发送画面失败（链路不可用）");
        }
        catch
        {
            // ignore
        }

        return Task.CompletedTask;
    }

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
                if (_hostPeer == null || !_hostAudioSync) return;
                peer = _hostPeer;
                sid = _hostSessionId;
                seq = ++_audioSeq;
            }

            var payload = ScreenExtendAudioCodec.Encode(sid, sampleRate, channels, seq, pcm);
            await _connections.SendToAsync(peer!, MessageType.ScreenExtendAudio, payload)
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
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
            case MessageType.ScreenExtendHostRequest:
                _ = HandleHostRequestAsync(peer, msg.Payload);
                break;
            case MessageType.ScreenExtendHostRequestResult:
                HandleHostRequestResult(msg.Payload);
                break;
        }
    }

    private async Task HandleHostRequestAsync(string fromPeer, byte[] payload)
    {
        var req = MessageCodec.DecodeJson<ScreenExtendHostRequestPayload>(payload);
        if (req == null) return;

        var requestId = string.IsNullOrWhiteSpace(req.RequestId)
            ? Guid.NewGuid().ToString("N")
            : req.RequestId;
        bool ok;
        string? error;
        try
        {
            if (req.Stop)
            {
                if (_hostPeer != null)
                    await StopAsync("remote-project-stop").ConfigureAwait(false);
                ok = true;
                error = null;
            }
            else if (OnHostRequestAsync == null)
            {
                ok = false;
                error = "对端不支持远程投影（请升级玄鸟）";
            }
            else
            {
                (ok, error) = await OnHostRequestAsync(fromPeer, req, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        try
        {
            var result = new ScreenExtendHostRequestResultPayload
            {
                RequestId = requestId,
                Ok = ok,
                Error = error
            };
            await _connections.SendToAsync(fromPeer, MessageType.ScreenExtendHostRequestResult,
                MessageCodec.JsonBytes(result)).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        if (ok && !req.Stop)
            Publish($"远程投影：已按 {fromPeer} 请求向 {(string.IsNullOrWhiteSpace(req.SinkPeer) ? fromPeer : req.SinkPeer)} 推流");
        else if (!ok)
            Publish($"远程投影请求失败：{error}");
    }

    private void HandleHostRequestResult(byte[] payload)
    {
        var result = MessageCodec.DecodeJson<ScreenExtendHostRequestResultPayload>(payload);
        if (result == null || string.IsNullOrWhiteSpace(result.RequestId)) return;
        if (_hostRequestWait.TryRemove(result.RequestId, out var tcs))
            tcs.TrySetResult(result);
    }

    private async Task HandleOfferAsync(string peer, byte[] payload)
    {
        var offer = MessageCodec.DecodeJson<ScreenExtendOfferPayload>(payload);
        if (offer == null || !Guid.TryParse(offer.SessionId, out var sid))
            return;

        string? hostBusyPeer;
        lock (_gate)
        {
            hostBusyPeer = _hostPeer;
            // 同对端已有 Sink：替换该路
            if (hostBusyPeer == null && _sinks.ContainsKey(peer))
            {
                _sinks.Remove(peer);
                if (string.Equals(_activeSinkPeer, peer, StringComparison.OrdinalIgnoreCase))
                    _activeSinkPeer = _sinks.Keys.FirstOrDefault();
                SyncPublic_NoLock();
            }
        }

        if (hostBusyPeer != null)
        {
            var notice = ScreenExtendConflict.DescribeLocalRejectOffer(
                peer, ScreenExtendRole.Host, hostBusyPeer);
            Publish(notice);
            NotifyUser(notice);
            await RejectAsync(peer, offer.SessionId, ScreenExtendConflict.ReasonBusyHost).ConfigureAwait(false);
            return;
        }

        var accept = ShouldAcceptOffer?.Invoke(peer, offer) ?? true;
        if (!accept)
        {
            await RejectAsync(peer, offer.SessionId, ScreenExtendConflict.ReasonDeclined).ConfigureAwait(false);
            return;
        }

        var enrolled = false;
        lock (_gate)
        {
            if (_hostPeer != null)
                hostBusyPeer = _hostPeer;
            else
            {
                _sinks[peer] = new SinkSession
                {
                    Peer = peer,
                    SessionId = sid,
                    AudioSync = offer.AudioSync,
                    RemoteControl = offer.RemoteControl,
                    FrameSeq = 0
                };
                _activeSinkPeer ??= peer;
                SyncPublic_NoLock();
                enrolled = true;
            }
        }

        if (!enrolled)
        {
            var notice = ScreenExtendConflict.DescribeLocalRejectOffer(
                peer, ScreenExtendRole.Host, hostBusyPeer);
            Publish(notice);
            NotifyUser(notice);
            await RejectAsync(peer, offer.SessionId, ScreenExtendConflict.ReasonBusyHost).ConfigureAwait(false);
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
            try { SinkSessionReady?.Invoke(peer); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _sinks.Remove(peer);
                if (string.Equals(_activeSinkPeer, peer, StringComparison.OrdinalIgnoreCase))
                    _activeSinkPeer = _sinks.Keys.FirstOrDefault();
                SyncPublic_NoLock();
            }

            Publish($"投影接受失败：{ex.Message}");
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
            if (_hostPeer == null || _hostSessionId != sid) return;
            if (!string.Equals(_hostPeer, peer, StringComparison.OrdinalIgnoreCase)) return;
        }

        if (acc.SinkWidth > 0 && acc.SinkHeight > 0)
        {
            PeerSinkWidth = acc.SinkWidth;
            PeerSinkHeight = acc.SinkHeight;
            if (acc.SinkScalePercent > 0)
                PeerSinkScalePercent = acc.SinkScalePercent;
        }

        Publish($"投影：{peer} 已接受（{PeerSinkWidth}x{PeerSinkHeight}"
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
            if (_hostPeer == null || _hostSessionId != sid) return;
        }

        var notice = ScreenExtendConflict.DescribeRemoteReject(peer, rej.Reason);
        Publish(notice);
        NotifyUser(notice);
        EndHostLocal(peer, "rejected");
    }

    private void NotifyUser(string message)
    {
        try { UserNotice?.Invoke(message); } catch { /* ignore */ }
    }

    private void HandleStop(string peer, byte[] payload)
    {
        var stop = MessageCodec.DecodeJson<ScreenExtendStopPayload>(payload);
        if (stop == null || !Guid.TryParse(stop.SessionId, out var sid)) return;

        var asHost = false;
        var asSink = false;
        lock (_gate)
        {
            if (_hostPeer != null
                && _hostSessionId == sid
                && string.Equals(_hostPeer, peer, StringComparison.OrdinalIgnoreCase))
            {
                ResetHost_NoLock();
                SyncPublic_NoLock();
                asHost = true;
            }
            else if (_sinks.TryGetValue(peer, out var sink) && sink.SessionId == sid)
            {
                _sinks.Remove(peer);
                if (string.Equals(_activeSinkPeer, peer, StringComparison.OrdinalIgnoreCase))
                    _activeSinkPeer = _sinks.Keys.FirstOrDefault();
                SyncPublic_NoLock();
                asSink = true;
            }
        }

        if (!asHost && !asSink) return;
        Publish($"投影已结束：{stop.Reason}");
        FinishSession(peer, "remote-stop");
    }

    private void HandleFrame(string peer, byte[] payload)
    {
        SinkSession? session;
        lock (_gate)
        {
            if (!_sinks.TryGetValue(peer, out session)) return;
        }

        if (!ScreenExtendFrameCodec.TryDecode(payload, out var sid, out var w, out var h, out var seq,
                out var capturedUnixMs, out var jpeg))
            return;
        if (sid != session.SessionId || jpeg.Length == 0) return;

        var first = false;
        lock (_gate)
        {
            if (!_sinks.TryGetValue(peer, out session)) return;
            if (session.FrameSeq == 0) first = true;
            session.FrameSeq = seq;
        }

        if (first)
            Publish($"投影：正在接收 {peer} 画面 {w}x{h}");

        try { FrameReceived?.Invoke(jpeg.ToArray(), w, h, capturedUnixMs, peer); } catch { /* ignore */ }
    }

    private void HandleAudio(string peer, byte[] payload)
    {
        SinkSession? session;
        lock (_gate)
        {
            if (!_sinks.TryGetValue(peer, out session) || !session.AudioSync) return;
        }

        if (!ScreenExtendAudioCodec.TryDecode(payload, out var sid, out var rate, out var channels, out _, out var pcm))
            return;
        if (sid != session.SessionId || pcm.Length == 0) return;
        try { AudioReceived?.Invoke(pcm.ToArray(), rate, channels, peer); } catch { /* ignore */ }
    }

    private void HandleReverseInput(string peer, byte[] payload)
    {
        var input = MessageCodec.DecodeJson<ScreenExtendInputPayload>(payload);
        if (input == null || !Guid.TryParse(input.SessionId, out var sid)) return;
        lock (_gate)
        {
            if (_hostPeer == null || !_hostRemoteControl) return;
            if (_hostSessionId != sid) return;
            if (!string.Equals(_hostPeer, peer, StringComparison.OrdinalIgnoreCase)) return;
        }

        try { ReverseInputReceived?.Invoke(input); } catch { /* ignore */ }
    }

    private void OnPeerLinkChanged(string peer, bool outbound, bool inbound)
    {
        if (outbound || inbound) return;

        var endHost = false;
        var endSink = false;
        lock (_gate)
        {
            if (_hostPeer != null && string.Equals(_hostPeer, peer, StringComparison.OrdinalIgnoreCase))
            {
                ResetHost_NoLock();
                SyncPublic_NoLock();
                endHost = true;
            }
            else if (_sinks.ContainsKey(peer))
            {
                _sinks.Remove(peer);
                if (string.Equals(_activeSinkPeer, peer, StringComparison.OrdinalIgnoreCase))
                    _activeSinkPeer = _sinks.Keys.FirstOrDefault();
                SyncPublic_NoLock();
                endSink = true;
            }
        }

        if (!endHost && !endSink) return;
        Publish($"投影：对端 {peer} 断开");
        FinishSession(peer, "disconnect");
    }

    private void ClearHostLocal()
    {
        lock (_gate)
        {
            ResetHost_NoLock();
            SyncPublic_NoLock();
        }
    }

    private void EndHostLocal(string peer, string reason)
    {
        lock (_gate)
        {
            ResetHost_NoLock();
            SyncPublic_NoLock();
        }

        FinishSession(peer, reason);
    }

    private void EndSinkLocal(string peer, string reason)
    {
        lock (_gate)
        {
            _sinks.Remove(peer);
            if (string.Equals(_activeSinkPeer, peer, StringComparison.OrdinalIgnoreCase))
                _activeSinkPeer = _sinks.Keys.FirstOrDefault();
            SyncPublic_NoLock();
        }

        FinishSession(peer, reason);
    }

    private void FinishSession(string? peer, string reason)
    {
        try { SessionEnded?.Invoke(peer); } catch { /* ignore */ }
        Publish($"投影会话结束（{reason}）" + (string.IsNullOrEmpty(peer) ? "" : $" · {peer}"));
        // 清临时保活并拆除非持久链路，避免 MaintainLoop 经信令/中继重建控制/bulk 管道
        _ = ReleaseProjectionLinksAsync(peer);
    }

    private async Task ReleaseProjectionLinksAsync(string? peer)
    {
        try
        {
            await _connections.ReleaseAdHocPeerAsync(peer).ConfigureAwait(false);
            if (!IsActive)
                await _connections.DropNonPersistentPeersAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private void SyncPublic_NoLock()
    {
        if (_hostPeer != null)
        {
            Role = ScreenExtendRole.Host;
            Peer = _hostPeer;
            SessionId = _hostSessionId;
            AudioSync = _hostAudioSync;
            RemoteControl = _hostRemoteControl;
            return;
        }

        if (_sinks.Count > 0)
        {
            Role = ScreenExtendRole.Sink;
            if (string.IsNullOrEmpty(_activeSinkPeer) || !_sinks.ContainsKey(_activeSinkPeer))
                _activeSinkPeer = _sinks.Keys.First();
            var s = _sinks[_activeSinkPeer!];
            Peer = s.Peer;
            SessionId = s.SessionId;
            AudioSync = s.AudioSync;
            RemoteControl = s.RemoteControl;
            return;
        }

        Role = ScreenExtendRole.None;
        Peer = null;
        SessionId = Guid.Empty;
        AudioSync = false;
        RemoteControl = false;
    }

    private void ResetHost_NoLock()
    {
        _hostPeer = null;
        _hostSessionId = Guid.Empty;
        _hostAudioSync = false;
        _hostRemoteControl = false;
        _frameSeq = 0;
        _audioSeq = 0;
        PeerSinkWidth = 0;
        PeerSinkHeight = 0;
        PeerSinkScalePercent = 0;
    }

    private void ResetAll_NoLock()
    {
        ResetHost_NoLock();
        _sinks.Clear();
        _activeSinkPeer = null;
        SyncPublic_NoLock();
    }

    private void Publish(string msg)
    {
        try { StatusMessage?.Invoke(msg); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (IsActive || GetSinkPeers().Count > 0)
                StopAsync("dispose").GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        _connections.MessageReceived -= OnPeerMessage;
        _connections.PeerLinkChanged -= OnPeerLinkChanged;
        lock (_gate)
            ResetAll_NoLock();
    }
}
