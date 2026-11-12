namespace FlyScreen.Models;

/// <summary>
/// 远程设备绑定：挂在某台本地设备（LocalDeviceId）的远程池中。
/// 本机 settings 持久化；布局电脑之间经 <c>RemoteBindingSync</c> 权威同步（不进 LayoutSync 槽位报文）。
/// </summary>
public sealed class RemoteBinding
{
    public string RemoteId { get; set; } = Guid.NewGuid().ToString("N");
    public string RemoteName { get; set; } = "";
    /// <summary>绑定的本地设备稳定名（与 MachineSlots 中的机器名对齐）。</summary>
    public string BoundLocalDeviceId { get; set; } = "";
    /// <summary>对应 ConnectionEndpoint.Id；空表示手动地址。</summary>
    public string EndpointId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    /// <summary>0=P2P 1=中转。</summary>
    public int Mode { get; set; } = 1;
    /// <summary>0=已绑定 1=投影中 2=离线 3=错误。</summary>
    public int State { get; set; }
    public int? RttMs { get; set; }

    public const int ModeP2P = 0;
    public const int ModeRelay = 1;

    public const int StateBound = 0;
    public const int StateProjecting = 1;
    public const int StateOffline = 2;
    public const int StateError = 3;
}
