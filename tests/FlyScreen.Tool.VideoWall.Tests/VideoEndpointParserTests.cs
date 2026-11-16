using FlyScreen.Tool.VideoWall.Sources;
using Xunit;

namespace FlyScreen.Tool.VideoWall.Tests;

public class VideoEndpointParserTests
{
    [Fact]
    public void Parse_PasswordContainingAt_SplitsByIpv4Host()
    {
        var r = VideoEndpointParser.ParseAndNormalize(
            "rtsp://admin:Jf@123456@10.10.1.246:554/cam/realmonitor?channel=1&subtype=0");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("admin", r.User);
        Assert.Equal("Jf@123456", r.Password);
        Assert.NotNull(r.Endpoint);
        Assert.Equal("rtsp", r.Endpoint.Protocol);
        Assert.StartsWith("rtsp://10.10.1.246:554/cam/realmonitor", r.Endpoint.Address, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jf@", r.Endpoint.Address, StringComparison.Ordinal);
        Assert.DoesNotContain("admin:", r.Endpoint.Address, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SingleAt_StillWorks()
    {
        var r = VideoEndpointParser.ParseAndNormalize(
            "rtsp://admin:pass@192.168.1.64:554/Streaming/Channels/101");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("admin", r.User);
        Assert.Equal("pass", r.Password);
        Assert.Contains("192.168.1.64", r.Endpoint!.Address, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PasswordContainingAt_WithHostname()
    {
        var r = VideoEndpointParser.ParseAndNormalize(
            "rtsp://user:p@ss@cam.example.com:554/stream");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("user", r.User);
        Assert.Equal("p@ss", r.Password);
        Assert.Contains("cam.example.com", r.Endpoint!.Address, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PercentEncodedAt_InPassword()
    {
        var r = VideoEndpointParser.ParseAndNormalize(
            "rtsp://admin:Jf%40123456@10.10.1.246:554/live");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("admin", r.User);
        Assert.Equal("Jf@123456", r.Password);
    }

    [Fact]
    public void Redact_PasswordContainingAt_HidesUserInfo()
    {
        var redacted = VideoEndpointParser.RedactAddress(
            "rtsp://admin:Jf@123456@10.10.1.246:554/cam/realmonitor?channel=1&subtype=0");

        Assert.StartsWith("rtsp://***@10.10.1.246", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Jf@123456", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MulticastUdp_KeepsAtMarker()
    {
        var r = VideoEndpointParser.ParseAndNormalize("udp://@239.1.1.1:5000");

        Assert.True(r.Ok, r.Message);
        Assert.True(r.IsMulticast);
        Assert.Null(r.User);
    }
}
