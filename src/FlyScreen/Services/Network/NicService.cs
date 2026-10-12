using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

public static class NicService
{
    public static IReadOnlyList<NicInfo> ListIpv4Nics()
    {
        var list = new List<NicInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (nic.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus != OperationalStatus.Unknown)
                continue;

            var props = nic.GetIPProperties();
            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(uni.Address))
                    continue;

                list.Add(new NicInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    IPv4 = uni.Address.ToString(),
                    SubnetMask = uni.IPv4Mask?.ToString() ?? "255.255.255.0",
                    IsUp = nic.OperationalStatus == OperationalStatus.Up
                });
            }
        }

        return list.OrderByDescending(n => n.IsUp).ThenBy(n => n.Name).ToList();
    }

    public static NicInfo? ResolvePreferred(string? nicId, string? ip)
    {
        var all = ListIpv4Nics();
        if (!string.IsNullOrWhiteSpace(ip))
        {
            var byIp = all.FirstOrDefault(n => n.IPv4 == ip);
            if (byIp != null) return byIp;
        }

        if (!string.IsNullOrWhiteSpace(nicId))
        {
            var byId = all.FirstOrDefault(n => n.Id == nicId);
            if (byId != null) return byId;
        }

        // 优先以太网/非虚拟网卡
        return all.FirstOrDefault(n =>
                   !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                   !n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) &&
                   !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                   !n.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase) &&
                   !n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault();
    }

    public static bool SameSubnet(string ipA, string maskA, string ipB)
    {
        try
        {
            var a = IPAddress.Parse(ipA).GetAddressBytes();
            var m = IPAddress.Parse(maskA).GetAddressBytes();
            var b = IPAddress.Parse(ipB).GetAddressBytes();
            for (var i = 0; i < 4; i++)
            {
                if ((a[i] & m[i]) != (b[i] & m[i]))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> ResolveHostAsync(string hostOrIp, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(hostOrIp, out _))
            return hostOrIp;

        try
        {
            var entry = await Dns.GetHostEntryAsync(hostOrIp, ct).ConfigureAwait(false);
            var v4 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
