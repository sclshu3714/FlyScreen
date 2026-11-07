using FlyScreen.Services.Network;
using Xunit;

namespace FlyScreen.Core.Tests;

public class DiscoveryEndpointTests
{
    [Fact]
    public void OrderConnectIps_PrefersUdpSourceOverAdvertised()
    {
        var ips = DiscoveryService.OrderConnectIps("10.0.0.8", "192.168.31.99");
        Assert.Equal(["192.168.31.99", "10.0.0.8"], ips);
    }

    [Fact]
    public void OrderConnectIps_SkipsUnusableAndDuplicates()
    {
        var ips = DiscoveryService.OrderConnectIps("0.0.0.0", "192.168.31.99");
        Assert.Equal(["192.168.31.99"], ips);

        ips = DiscoveryService.OrderConnectIps("192.168.31.99", "192.168.31.99");
        Assert.Equal(["192.168.31.99"], ips);

        ips = DiscoveryService.OrderConnectIps("127.0.0.1", null);
        Assert.Empty(ips);
    }
}
