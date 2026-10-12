using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

public static class NicService
{
    private static readonly string[] VirtualKeywords =
    [
        "VirtualBox", "VMware", "Hyper-V", "HyperV", "WSL", "Docker", "vEthernet",
        "Bluetooth", "Teredo", "ISATAP", "NPCAP", "Npcap", "TAP-Windows", "TAP Adapter",
        "TUN", "WireGuard", "OpenVPN", "Hamachi", "Tailscale", "ZeroTier", "Cisco AnyConnect",
        "Fortinet", "Check Point", "VPN", "Pseudo", "Hosted Network", "Wi-Fi Direct",
        "Microsoft Wi-Fi Direct", "Kernel Debug", "Loopback", "Virtual Adapter"
    ];

    public static IReadOnlyList<NicInfo> ListIpv4Nics()
        => EnumerateIpv4Nics(usableOnly: false);

    /// <summary>仅保留可通信的物理/无线网卡地址，去掉 APIPA、虚拟网卡等。</summary>
    public static IReadOnlyList<NicInfo> ListUsableIpv4Nics()
    {
        var usable = EnumerateIpv4Nics(usableOnly: true);
        if (usable.Count > 0) return usable;
        // 过滤后为空时回退：仍去掉环回与 APIPA，避免下拉没有选项
        return EnumerateIpv4Nics(usableOnly: false)
            .Where(n => n.IsUp && !IsApipa(n.IPv4))
            .ToList();
    }

    private static IReadOnlyList<NicInfo> EnumerateIpv4Nics(bool usableOnly)
    {
        var list = new List<NicInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (usableOnly)
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Unknown)
                    continue;
                if (IsVirtualAdapter(nic))
                    continue;
            }
            else if (nic.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus != OperationalStatus.Unknown)
            {
                continue;
            }

            var props = nic.GetIPProperties();
            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(uni.Address))
                    continue;

                var ip = uni.Address.ToString();
                if (usableOnly && !IsUsableUnicast(uni.Address))
                    continue;

                list.Add(new NicInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    IPv4 = ip,
                    SubnetMask = uni.IPv4Mask?.ToString() ?? "255.255.255.0",
                    IsUp = nic.OperationalStatus == OperationalStatus.Up
                });
            }
        }

        return list
            .OrderByDescending(n => n.IsUp)
            .ThenBy(n => PreferWifiOrEthernet(n) ? 0 : 1)
            .ThenBy(n => n.Name)
            .ToList();
    }

    private static bool PreferWifiOrEthernet(NicInfo n)
        => n.Name.Contains("以太网", StringComparison.OrdinalIgnoreCase)
           || n.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
           || n.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
           || n.Name.Contains("WLAN", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        var text = nic.Name + " " + nic.Description;
        foreach (var key in VirtualKeywords)
        {
            if (text.Contains(key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp)
            return true;
        if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211
            && nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
            return true;

        return false;
    }

    private static bool IsUsableUnicast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 0) return false;           // 0.0.0.0/8
        if (bytes[0] == 169 && bytes[1] == 254) return false; // APIPA
        if (bytes[0] == 224) return false;         // multicast
        if (bytes[0] >= 240) return false;         // reserved / broadcast-ish
        return true;
    }

    private static bool IsApipa(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var b = addr.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    public static NicInfo? ResolvePreferred(string? nicId, string? ip)
    {
        var all = ListUsableIpv4Nics();
        if (all.Count == 0) all = ListIpv4Nics();
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
