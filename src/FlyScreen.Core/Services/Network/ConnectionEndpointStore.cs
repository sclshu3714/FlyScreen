using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>连接配置行与远程绑定的本机持久化辅助（跨机见 RemoteBindingSync）。</summary>
public static class ConnectionEndpointStore
{
    /// <summary>
    /// 若绑定记录有地址但 ConnectionEndpoints 已丢行，补回一条配置（恢复 UI / 再测 / 建连）。
    /// </summary>
    public static int EnsureFromBindings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnsureCollections();
        var added = 0;
        foreach (var b in settings.RemoteBindings)
        {
            var addr = (b.Endpoint ?? "").Trim();
            if (string.IsNullOrWhiteSpace(addr) || addr is "—" or "-")
                continue;

            var exists = settings.ConnectionEndpoints.Any(ep =>
                (!string.IsNullOrWhiteSpace(b.EndpointId)
                 && string.Equals(ep.Id, b.EndpointId, StringComparison.OrdinalIgnoreCase))
                || string.Equals((ep.Address ?? "").Trim(), addr, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(b.RemoteName)
                    && string.Equals(ep.VerifiedPeerName, b.RemoteName, StringComparison.OrdinalIgnoreCase)));
            if (exists) continue;

            settings.ConnectionEndpoints.Add(new ConnectionEndpoint
            {
                Id = string.IsNullOrWhiteSpace(b.EndpointId)
                    ? Guid.NewGuid().ToString("N")
                    : b.EndpointId.Trim(),
                Kind = b.Mode == RemoteBinding.ModeRelay
                    ? ConnectionEndpoint.KindRelay
                    : ConnectionEndpoint.KindRemote,
                Address = addr,
                VerifiedPeerName = b.RemoteName?.Trim() ?? "",
                // 补回的行未在本机测试，不能标成「测试通过」
                State = ConnectionEndpoint.StateDisconnected,
                LastRttMs = null
            });
            added++;
        }

        if (added > 0)
            RemoteBindingSyncHelper.DeduplicateEndpoints(settings);

        return added;
    }
}
