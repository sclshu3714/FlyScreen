using System.Text.Json;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Tools.SharedDevices;

/// <summary>
/// 共享设备申请 / 授权会话。扬声器：系统回环 → ToolData PCM → 对端播放（虚拟声卡后续接入）。
/// </summary>
public sealed class SharedDevicesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly ConnectionManager _connections;
    private readonly AppSettings _settings;
    private readonly ToolCatalog _catalog;
    private readonly SharedDevicesRememberStore _remember;
    private readonly SharedDevicesSelectionStore _selection;
    private ISharedDevicesHardware _hardware = NullSharedDevicesHardware.Instance;

    private SharedDevicesSession? _session;
    private int _audioSeq;
    private int _audioPending;

    public SharedDevicesService(
        ConnectionManager connections,
        AppSettings settings,
        ToolCatalog catalog,
        SharedDevicesRememberStore? remember = null,
        SharedDevicesSelectionStore? selection = null)
    {
        _connections = connections;
        _settings = settings;
        _catalog = catalog;
        _remember = remember ?? new SharedDevicesRememberStore();
        _selection = selection ?? new SharedDevicesSelectionStore();
        _connections.MessageReceived += OnMessage;
    }

    public event Action? SessionChanged;
    public event Action<SharedDevicesIncomingRequest>? IncomingRequest;
    public event Action<string>? StatusMessage;
    /// <summary>提供方本机选用设备变更（管理/接收申请切换）。</summary>
    public event Action? SelectedDevicesChanged;
    /// <summary>提供方收到申请端扬声器 PCM（s16le）→ 本机播放。</summary>
    public event Action<byte[], int, int>? AudioReceived;
    /// <summary>申请端收到提供方麦克风 PCM。</summary>
    public event Action<byte[], int, int>? MicAudioReceived;
    /// <summary>申请端收到提供方摄像头 JPEG。</summary>
    public event Action<byte[]>? CameraJpegReceived;
    /// <summary>提供方：对端请求开始/停止某路本机测试（kind + start）。</summary>
    public event Action<string, bool>? ProviderTestCommand;

    /// <summary>挂接本机硬件枚举（Android / Windows 提供方）。</summary>
    public void AttachHardware(ISharedDevicesHardware hardware)
        => _hardware = hardware ?? NullSharedDevicesHardware.Instance;

    public IReadOnlyList<SharedLocalDevice> EnumerateLocalDevices(string kind)
    {
        try { return _hardware.Enumerate(kind); }
        catch (Exception ex)
        {
            AppLog.Warn("EnumerateLocalDevices: " + ex.Message);
            return [];
        }
    }

    /// <summary>当前选用 Id；无记录时回落默认或列表首项。</summary>
    public string? GetSelectedDeviceId(string kind)
    {
        var list = EnumerateLocalDevices(kind);
        var saved = _selection.Get(kind);
        if (!string.IsNullOrEmpty(saved) && list.Any(d => d.Id == saved))
            return saved;
        var def = list.FirstOrDefault(d => d.IsDefault) ?? list.FirstOrDefault();
        return def?.Id;
    }

    public SharedLocalDevice? GetSelectedDevice(string kind)
    {
        var id = GetSelectedDeviceId(kind);
        if (string.IsNullOrEmpty(id)) return null;
        return EnumerateLocalDevices(kind).FirstOrDefault(d => d.Id == id);
    }

    public void SetSelectedDevice(string kind, string? deviceId)
    {
        if (!SharedDeviceKinds.IsValid(kind)) return;
        var prev = _selection.Get(kind);
        var next = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        if (string.Equals(prev, next, StringComparison.Ordinal))
            return;
        _selection.Set(kind, next);
        try { SelectedDevicesChanged?.Invoke(); } catch { /* ignore */ }
    }

    public void ApplyDeviceSelections(IReadOnlyDictionary<string, string> byKind)
    {
        foreach (var kv in byKind)
            SetSelectedDevice(kv.Key, kv.Value);
    }

    public void ClearRemember(string? applicantPeer = null) => _remember.Clear(applicantPeer);

    public IReadOnlyList<(string Applicant, string[] Kinds)> ListRemembered()
    {
        // 暴露给管理页；内部 Reload 保证一致
        _remember.Reload();
        return _remember.ListEntries();
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _session is { Phase: SharedDevicesSessionPhase.Requesting or SharedDevicesSessionPhase.Active };
        }
    }

    public SharedDevicesSession? CurrentSession
    {
        get
        {
            lock (_gate)
                return _session;
        }
    }

    public IReadOnlyList<SharedDevicesTargetInfo> ListApplicantTargets()
    {
        var local = LocalPeerIdentity.MachineName;
        var result = new List<SharedDevicesTargetInfo>();
        foreach (var slot in _settings.MachineSlots)
        {
            var name = slot?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, local, StringComparison.OrdinalIgnoreCase)) continue;

            var status = _connections.GetPeerStatus(name);
            var info = _connections.GetPeerRemoteInfo(name);
            var isDesktop = info == null
                || string.IsNullOrWhiteSpace(info.DeviceClass)
                || info.DeviceClass.Equals(PeerDeviceClass.Desktop, StringComparison.OrdinalIgnoreCase);
            var toolInstalled = RemoteHasTool(info);
            // 电脑 / 手机 / 平板凡已装工具且具备扬声器、麦克风、摄像头之一，均可作接收端外设
            result.Add(new SharedDevicesTargetInfo
            {
                PeerName = name,
                Connected = status.Bidirectional || status.AnyAlive,
                IsDesktop = isDesktop,
                ToolInstalled = toolInstalled,
                ProvidableKinds = [.. SharedDeviceKinds.All]
            });
        }

        return result;
    }

    /// <summary>申请端确认启动：向目标发 request。</summary>
    public async Task<(bool Ok, string Message)> StartRequestAsync(string peerName, IEnumerable<string> kinds)
    {
        if (!_catalog.IsInstalled(ToolIds.SharedDevices))
            return (false, "请先安装共享设备工具");

        var peer = peerName?.Trim() ?? "";
        if (string.IsNullOrEmpty(peer))
            return (false, "请选择目标设备");

        var kindList = kinds
            .Where(SharedDeviceKinds.IsValid)
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        if (kindList.Length == 0)
            return (false, "请至少选择一种设备");

        var info = _connections.GetPeerRemoteInfo(peer);
        if (!RemoteHasTool(info))
            return (false, $"「{peer}」未安装共享设备工具");

        if (!_connections.GetPeerStatus(peer).AnyAlive)
            return (false, $"「{peer}」未连接");

        lock (_gate)
        {
            if (_session != null)
                return (false, "已有进行中的共享会话，请先停止");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var body = new SharedDevicesRequestBody { Kinds = kindList };
        var env = new ToolEnvelope
        {
            ToolId = ToolIds.SharedDevices,
            Op = SharedDevicesOps.Request,
            SchemaVersion = 1,
            SessionId = sessionId,
            BodyJson = JsonSerializer.Serialize(body, JsonOptions)
        };

        var bytes = MessageCodec.JsonBytes(env);
        if (!await _connections.SendToAsync(peer, MessageType.ToolSignal, bytes).ConfigureAwait(false))
            return (false, "发送申请失败");

        lock (_gate)
        {
            _session = new SharedDevicesSession
            {
                SessionId = sessionId,
                PeerName = peer,
                Kinds = kindList,
                IsApplicant = true,
                Phase = SharedDevicesSessionPhase.Requesting
            };
        }

        RaiseSessionChanged();
        Publish($"已向「{peer}」申请：{string.Join("、", kindList)}");
        return (true, "已发送申请");
    }

    public async Task StopAsync(string? reason = null)
    {
        SharedDevicesSession? snap;
        lock (_gate)
        {
            snap = _session;
            if (snap == null) return;
            _session = null;
        }

        var body = new SharedDevicesCloseBody { Kinds = snap.Kinds, Reason = reason };
        var env = new ToolEnvelope
        {
            ToolId = ToolIds.SharedDevices,
            Op = SharedDevicesOps.SessionClose,
            SchemaVersion = 1,
            SessionId = snap.SessionId,
            BodyJson = JsonSerializer.Serialize(body, JsonOptions)
        };
        try
        {
            await _connections.SendToAsync(snap.PeerName, MessageType.ToolSignal, MessageCodec.JsonBytes(env))
                .ConfigureAwait(false);
        }
        catch { /* ignore */ }

        RaiseSessionChanged();
        Publish("共享设备已停止");
    }

    public async Task AcceptIncomingAsync(
        SharedDevicesIncomingRequest req,
        bool remember,
        IReadOnlyDictionary<string, string>? deviceIds = null)
    {
        if (!_catalog.IsInstalled(ToolIds.SharedDevices))
            return;

        // 仅勾选「记住适配」才写入；未勾选则下次仍须手动同意
        if (remember)
            _remember.Remember(req.FromPeer, req.Kinds);

        if (deviceIds != null && deviceIds.Count > 0)
            ApplyDeviceSelections(deviceIds);

        var body = new SharedDevicesAcceptBody { Kinds = req.Kinds };
        var env = new ToolEnvelope
        {
            ToolId = ToolIds.SharedDevices,
            Op = SharedDevicesOps.RequestAccept,
            SchemaVersion = 1,
            SessionId = req.SessionId,
            BodyJson = JsonSerializer.Serialize(body, JsonOptions)
        };
        await _connections.SendToAsync(req.FromPeer, MessageType.ToolSignal, MessageCodec.JsonBytes(env))
            .ConfigureAwait(false);

        lock (_gate)
        {
            _session = new SharedDevicesSession
            {
                SessionId = req.SessionId,
                PeerName = req.FromPeer,
                Kinds = req.Kinds,
                IsApplicant = false,
                Phase = SharedDevicesSessionPhase.Active
            };
        }

        RaiseSessionChanged();
        Publish($"已同意「{req.FromPeer}」使用：{string.Join("、", req.Kinds)}");
    }

    public async Task RejectIncomingAsync(SharedDevicesIncomingRequest req, string reason = "denied")
    {
        var body = new SharedDevicesRejectBody { Kinds = req.Kinds, Reason = reason };
        var env = new ToolEnvelope
        {
            ToolId = ToolIds.SharedDevices,
            Op = SharedDevicesOps.RequestReject,
            SchemaVersion = 1,
            SessionId = req.SessionId,
            BodyJson = JsonSerializer.Serialize(body, JsonOptions)
        };
        await _connections.SendToAsync(req.FromPeer, MessageType.ToolSignal, MessageCodec.JsonBytes(env))
            .ConfigureAwait(false);
        Publish($"已拒绝「{req.FromPeer}」的共享申请");
    }

    /// <summary>申请端：把系统声音 PCM 发到提供方扬声器。</summary>
    public void PostAudio(byte[] pcm, int sampleRate, int channels)
        => PostPcm(SharedDevicesMediaCodec.KindSpeakerPcm, applicantSide: true, pcm, sampleRate, channels);

    /// <summary>提供方：把本机麦克风 PCM 发到申请端（测试/共享上行）。</summary>
    public void PostMicAudio(byte[] pcm, int sampleRate, int channels)
        => PostPcm(SharedDevicesMediaCodec.KindMicPcm, applicantSide: false, pcm, sampleRate, channels);

    /// <summary>提供方：把本机摄像头 JPEG 发到申请端。</summary>
    public void PostCameraJpeg(byte[] jpeg)
    {
        if (jpeg.Length == 0) return;
        string? peer;
        lock (_gate)
        {
            if (_session is not { Phase: SharedDevicesSessionPhase.Active, IsApplicant: false })
                return;
            peer = _session.PeerName;
        }

        try
        {
            _connections.SendLatest(peer!, MessageType.ToolData, SharedDevicesMediaCodec.EncodeCameraJpeg(jpeg));
        }
        catch { /* ignore */ }
    }

    /// <summary>申请端请求对端开启/停止某路测试（摄像头/麦克风/扬声器）。</summary>
    public async Task<(bool Ok, string Message)> RequestPeerTestAsync(string kind, bool start)
    {
        if (!SharedDeviceKinds.IsValid(kind))
            return (false, "未知设备种类");

        SharedDevicesSession? snap;
        lock (_gate) snap = _session;
        if (snap is not { Phase: SharedDevicesSessionPhase.Active, IsApplicant: true })
            return (false, "请先启动共享设备并等待对端同意");
        if (!snap.Kinds.Any(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase)))
            return (false, $"当前会话未包含「{kind}」");

        var env = new ToolEnvelope
        {
            ToolId = ToolIds.SharedDevices,
            Op = start ? SharedDevicesOps.TestStart : SharedDevicesOps.TestStop,
            SchemaVersion = 1,
            SessionId = snap.SessionId,
            BodyJson = JsonSerializer.Serialize(new SharedDevicesTestBody { Kind = kind }, JsonOptions)
        };
        var ok = await _connections.SendToAsync(snap.PeerName, MessageType.ToolSignal, MessageCodec.JsonBytes(env))
            .ConfigureAwait(false);
        return ok
            ? (true, start ? "已请求对端开始测试" : "已请求对端停止测试")
            : (false, "发送失败");
    }

    public void PublishStatus(string msg) => Publish(msg);

    private void PostPcm(byte mediaKind, bool applicantSide, byte[] pcm, int sampleRate, int channels)
    {
        if (pcm.Length == 0 || sampleRate <= 0 || channels is < 1 or > 2)
            return;
        if (Interlocked.Increment(ref _audioPending) > 8)
        {
            Interlocked.Decrement(ref _audioPending);
            return;
        }

        _ = SendPcmAsync(mediaKind, applicantSide, pcm, sampleRate, channels);
    }

    private async Task SendPcmAsync(byte mediaKind, bool applicantSide, byte[] pcm, int sampleRate, int channels)
    {
        try
        {
            string? peer;
            Guid sid;
            int seq;
            lock (_gate)
            {
                if (_session is not { Phase: SharedDevicesSessionPhase.Active })
                    return;
                if (_session.IsApplicant != applicantSide)
                    return;
                peer = _session.PeerName;
                if (!Guid.TryParseExact(_session.SessionId, "N", out sid)
                    && !Guid.TryParse(_session.SessionId, out sid))
                    sid = Guid.Empty;
                seq = ++_audioSeq;
            }

            var payload = SharedDevicesMediaCodec.EncodePcm(mediaKind, sid, sampleRate, channels, seq, pcm);
            _connections.SendLatest(peer!, MessageType.ToolData, payload);
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch
        {
            // 单块失败不拆会话
        }
        finally
        {
            Interlocked.Decrement(ref _audioPending);
        }
    }

    private void OnMessage(string peer, NetMessage msg)
    {
        if (msg.Type == MessageType.ToolData)
        {
            HandleToolData(peer, msg.Payload);
            return;
        }

        if (msg.Type != MessageType.ToolSignal)
            return;

        ToolEnvelope? env;
        try
        {
            env = MessageCodec.DecodeJson<ToolEnvelope>(msg.Payload);
        }
        catch
        {
            return;
        }

        if (env == null
            || !string.Equals(env.ToolId, ToolIds.SharedDevices, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_catalog.IsInstalled(ToolIds.SharedDevices)
            && !string.Equals(env.Op, SharedDevicesOps.RequestReject, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(env.Op, SharedDevicesOps.SessionClose, StringComparison.OrdinalIgnoreCase))
        {
            _ = RejectNotInstalledAsync(peer, env.SessionId);
            return;
        }

        switch (env.Op)
        {
            case SharedDevicesOps.Request:
                HandleRequest(peer, env);
                break;
            case SharedDevicesOps.RequestAccept:
                HandleAccept(peer, env);
                break;
            case SharedDevicesOps.RequestReject:
                HandleReject(peer, env);
                break;
            case SharedDevicesOps.SessionClose:
                HandleClose(peer, env);
                break;
            case SharedDevicesOps.TestStart:
                HandleProviderTest(peer, env, start: true);
                break;
            case SharedDevicesOps.TestStop:
                HandleProviderTest(peer, env, start: false);
                break;
        }
    }

    private void HandleProviderTest(string peer, ToolEnvelope env, bool start)
    {
        SharedDevicesSession? snap;
        lock (_gate) snap = _session;
        if (snap is not { Phase: SharedDevicesSessionPhase.Active, IsApplicant: false })
            return;
        if (!string.Equals(snap.PeerName, peer, StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(snap.SessionId, env.SessionId, StringComparison.OrdinalIgnoreCase))
            return;

        var body = DecodeBody<SharedDevicesTestBody>(env.BodyJson);
        var kind = body?.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!SharedDeviceKinds.IsValid(kind))
            return;
        if (!snap.Kinds.Any(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase)))
            return;

        try { ProviderTestCommand?.Invoke(kind, start); }
        catch (Exception ex) { AppLog.Warn("ProviderTestCommand: " + ex.Message); }
    }

    private void HandleRequest(string peer, ToolEnvelope env)
    {
        var body = DecodeBody<SharedDevicesRequestBody>(env.BodyJson);
        var kinds = (body?.Kinds ?? [])
            .Where(SharedDeviceKinds.IsValid)
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        if (kinds.Length == 0)
        {
            _ = RejectIncomingAsync(new SharedDevicesIncomingRequest
            {
                FromPeer = peer,
                SessionId = env.SessionId,
                Kinds = []
            }, "no-device");
            return;
        }

        var incoming = new SharedDevicesIncomingRequest
        {
            FromPeer = peer,
            SessionId = env.SessionId,
            Kinds = kinds
        };

        if (_remember.IsRemembered(peer, kinds))
        {
            _ = AcceptIncomingAsync(incoming, remember: false);
            return;
        }

        try { IncomingRequest?.Invoke(incoming); }
        catch (Exception ex) { AppLog.Warn("IncomingRequest: " + ex.Message); }
    }

    private void HandleAccept(string peer, ToolEnvelope env)
    {
        lock (_gate)
        {
            if (_session == null
                || !_session.IsApplicant
                || !string.Equals(_session.SessionId, env.SessionId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_session.PeerName, peer, StringComparison.OrdinalIgnoreCase))
                return;
            _session.Phase = SharedDevicesSessionPhase.Active;
        }

        RaiseSessionChanged();
        Publish($"「{peer}」已同意，共享设备可用");
    }

    private void HandleReject(string peer, ToolEnvelope env)
    {
        var body = DecodeBody<SharedDevicesRejectBody>(env.BodyJson);
        lock (_gate)
        {
            if (_session != null
                && string.Equals(_session.SessionId, env.SessionId, StringComparison.OrdinalIgnoreCase))
                _session = null;
        }

        RaiseSessionChanged();
        Publish($"「{peer}」拒绝共享申请" + (string.IsNullOrEmpty(body?.Reason) ? "" : $"（{body!.Reason}）"));
    }

    private void HandleClose(string peer, ToolEnvelope env)
    {
        lock (_gate)
        {
            if (_session == null) return;
            if (!string.Equals(_session.SessionId, env.SessionId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_session.PeerName, peer, StringComparison.OrdinalIgnoreCase))
                return;
            _session = null;
        }

        RaiseSessionChanged();
        Publish($"「{peer}」结束了共享设备会话");
    }

    private void HandleToolData(string peer, byte[] payload)
    {
        SharedDevicesSession? snap;
        lock (_gate)
            snap = _session;
        if (snap is not { Phase: SharedDevicesSessionPhase.Active })
            return;
        if (!string.Equals(snap.PeerName, peer, StringComparison.OrdinalIgnoreCase))
            return;

        // 兼容旧包：无魔数时按「申请端→提供方扬声器」处理
        if (!SharedDevicesMediaCodec.TryDecode(payload, out var kind, out var body))
        {
            if (!snap.IsApplicant
                && ScreenExtendAudioCodec.TryDecode(payload, out _, out var r0, out var c0, out _, out var p0))
            {
                try { AudioReceived?.Invoke(p0.ToArray(), r0, c0); } catch { /* ignore */ }
            }

            return;
        }

        if (kind == SharedDevicesMediaCodec.KindSpeakerPcm && !snap.IsApplicant)
        {
            if (!ScreenExtendAudioCodec.TryDecode(body, out _, out var rate, out var channels, out _, out var pcm))
                return;
            try { AudioReceived?.Invoke(pcm.ToArray(), rate, channels); } catch { /* ignore */ }
            return;
        }

        if (kind == SharedDevicesMediaCodec.KindMicPcm && snap.IsApplicant)
        {
            if (!ScreenExtendAudioCodec.TryDecode(body, out _, out var rate, out var channels, out _, out var pcm))
                return;
            try { MicAudioReceived?.Invoke(pcm.ToArray(), rate, channels); } catch { /* ignore */ }
            return;
        }

        if (kind == SharedDevicesMediaCodec.KindCameraJpeg && snap.IsApplicant)
        {
            try { CameraJpegReceived?.Invoke(body.ToArray()); } catch { /* ignore */ }
        }
    }

    private async Task RejectNotInstalledAsync(string peer, string sessionId)
    {
        var env = new ToolEnvelope
        {
            ToolId = ToolIds.SharedDevices,
            Op = SharedDevicesOps.RequestReject,
            SchemaVersion = 1,
            SessionId = sessionId,
            BodyJson = JsonSerializer.Serialize(
                new SharedDevicesRejectBody { Reason = "not-installed" }, JsonOptions)
        };
        await _connections.SendToAsync(peer, MessageType.ToolSignal, MessageCodec.JsonBytes(env))
            .ConfigureAwait(false);
    }

    private static bool RemoteHasTool(PeerRemoteInfo? info)
    {
        if (info == null || info.Caps.Length == 0)
            return false;
        return info.Caps.Contains(PeerCaps.ToolSharedDevices, StringComparer.OrdinalIgnoreCase);
    }

    private static T? DecodeBody<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }

    private void RaiseSessionChanged()
    {
        try { SessionChanged?.Invoke(); } catch { /* ignore */ }
    }

    private void Publish(string msg)
    {
        AppLog.Info(msg);
        try { StatusMessage?.Invoke(msg); } catch { /* ignore */ }
    }
}
