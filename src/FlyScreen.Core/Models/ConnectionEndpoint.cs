using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FlyScreen.Models;

/// <summary>
/// 连接配置表中的可添加行。
/// 远程=一对一探测地址（Tailscale/VPN 等）；「测试」仅测连通，不进设备管理。
/// 刷新发现时把远程地址当作 UDP/握手探测目标，对端以「本地」出现在设备管理。
/// 服务=一对多；仅扫描到的真实设备可进设备管理。
/// </summary>
public sealed class ConnectionEndpoint : INotifyPropertyChanged
{
    private string _address = "";
    private string _displayName = "";
    private string _verifiedPeerName = "";
    private int? _lastRttMs;
    private int _state;
    private int _kind = KindRemote;
    private string _lastError = "";
    private List<RelayDiscoveredDevice> _discoveredDevices = [];

    /// <summary>稳定 Id（本机生成，不参与跨机协议）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>0=远程（一对一） 1=服务（一对多中转）。</summary>
    public int Kind
    {
        get => _kind;
        set
        {
            // ComboBox SelectedIndex=-1 等非法值钳到远程，避免行“空白/不可用”
            var v = value == KindRelay ? KindRelay : KindRemote;
            if (_kind == v) return;
            _kind = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasInventoryDevices));
        }
    }

    public void ToggleKind()
        => Kind = Kind == KindRelay ? KindRemote : KindRelay;

    /// <summary>地址：远程为 host:port / IP；中转为 wss://… 或中继入口。</summary>
    public string Address
    {
        get => _address;
        set
        {
            if (_address == value) return;
            _address = value ?? "";
            OnPropertyChanged();
            // 地址形态变化时提示类型，不强制改用户已选手动值以外的推断仅在为空地址时
            TryInferKindFromAddress();
        }
    }

    /// <summary>用户备注名（可选）。</summary>
    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName == value) return; _displayName = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>远程一对一：测试通过后解析到的对端真实名。</summary>
    public string VerifiedPeerName
    {
        get => _verifiedPeerName;
        set { if (_verifiedPeerName == value) return; _verifiedPeerName = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>中转一对多：最近一次刷新/测试扫描到的设备（空=尚未发现，不进设备管理）。</summary>
    public List<RelayDiscoveredDevice> DiscoveredDevices
    {
        get => _discoveredDevices;
        set { _discoveredDevices = value ?? []; OnPropertyChanged(); }
    }

    /// <summary>最近一次测试延迟（毫秒）；null 表示未测。</summary>
    public int? LastRttMs
    {
        get => _lastRttMs;
        set { if (_lastRttMs == value) return; _lastRttMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>0=未知 1=已连接 2=未连接 3=错误。</summary>
    public int State
    {
        get => _state;
        set { if (_state == value) return; _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    [JsonIgnore]
    public string KindLabel => Kind == KindRelay ? "服务" : "远程";

    /// <summary>最近一次失败原因（仅 UI）。</summary>
    public string LastError
    {
        get => _lastError;
        set { if (_lastError == value) return; _lastError = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>
    /// 测试状态文案。● 仅表示探测通过，真正会话要等加入/绑定布局后由 ConnectionManager 建连。
    /// </summary>
    [JsonIgnore]
    public string StatusText => State switch
    {
        StateConnected when Kind == KindRelay
            => FormatRelayConnectedStatus(),
        StateConnected => LastRttMs is int ms ? $"● 测试通过 {ms}ms" : "● 测试通过",
        StateDisconnected => "○ 未测试",
        StateError => string.IsNullOrWhiteSpace(LastError) ? "○ 无法连接" : $"○ {LastError}",
        _ => "○ 未测试"
    };

    /// <summary>中转是否已扫到可进设备管理的设备。远程配置永不直接进清单。</summary>
    [JsonIgnore]
    public bool HasInventoryDevices =>
        Kind == KindRelay
        && State == StateConnected
        && DiscoveredDevices is { Count: > 0 };

    public const int KindRemote = 0;
    public const int KindRelay = 1;

    public const int StateUnknown = 0;
    public const int StateConnected = 1;
    public const int StateDisconnected = 2;
    public const int StateError = 3;

    public void ClearVerification()
    {
        VerifiedPeerName = "";
        DiscoveredDevices = [];
        LastRttMs = null;
    }

    private string FormatRelayConnectedStatus()
    {
        var n = DiscoveredDevices?.Count ?? 0;
        if (n > 0)
        {
            return LastRttMs is int ms
                ? $"● 已注册 · {n} 台设备 · {ms}ms"
                : $"● 已注册 · {n} 台设备";
        }

        return LastRttMs is int rtt
            ? $"● 已注册 · {rtt}ms（等待其它设备）"
            : "● 已注册（等待其它设备）";
    }

    public void TryInferKindFromAddress()
    {
        var raw = (_address ?? "").Trim();
        if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            Kind = KindRelay;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
