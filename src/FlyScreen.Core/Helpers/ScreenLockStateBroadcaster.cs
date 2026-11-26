using FlyScreen.Services.Network;

namespace FlyScreen.Helpers;

/// <summary>本机锁屏状态变化时广播 <see cref="MessageType.ScreenLockState"/>。</summary>
public static class ScreenLockStateBroadcaster
{
    public static void Broadcast(ConnectionManager connections, bool locked)
    {
        try
        {
            var payload = MessageCodec.JsonBytes(new ScreenLockStatePayload
            {
                MachineName = LocalPeerIdentity.MachineName,
                Locked = locked
            });
            _ = connections.BroadcastAsync(MessageType.ScreenLockState, payload, requiredCap: null);
        }
        catch
        {
            // ignore
        }
    }
}
