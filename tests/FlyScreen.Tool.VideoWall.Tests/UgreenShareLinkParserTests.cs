using FlyScreen.Tool.VideoWall.Sources;
using Xunit;

namespace FlyScreen.Tool.VideoWall.Tests;

public class UgreenShareLinkParserTests
{
    [Fact]
    public void Parse_PortalLink_ExtractsAliasAndId()
    {
        var ok = UgreenShareLinkParser.TryParse(
            "https://ug.link/sclshu/filemgr/share-download/?id=9e0a4d202e6b4d619cfe42cc56113a04",
            out var link);

        Assert.True(ok);
        Assert.Equal("sclshu", link.Alias);
        Assert.Equal("9e0a4d202e6b4d619cfe42cc56113a04", link.ShareId);
        Assert.Null(link.DeviceBase);
        Assert.Null(link.Password);
    }

    [Fact]
    public void Parse_WwwPortal_SameAsPortal()
    {
        var ok = UgreenShareLinkParser.TryParse(
            "https://www.ug.link/sclshu/filemgr/share-download/?id=abc123&foo=1",
            out var link);

        Assert.True(ok);
        Assert.Equal("sclshu", link.Alias);
        Assert.Equal("abc123", link.ShareId);
    }

    [Fact]
    public void Parse_DeviceHost_UsesOrigin()
    {
        var ok = UgreenShareLinkParser.TryParse(
            "https://sclshu.cn65.ug.link/filemgr/share-download/?id=abc123",
            out var link);

        Assert.True(ok);
        Assert.Null(link.Alias);
        Assert.Equal("https://sclshu.cn65.ug.link", link.DeviceBase);
    }

    [Fact]
    public void Parse_LanOrigin_KeepsPortAndPassword()
    {
        var ok = UgreenShareLinkParser.TryParse(
            "https://192.168.1.5:9443/filemgr/share-download/?id=abc123&password=secret",
            out var link);

        Assert.True(ok);
        Assert.Equal("https://192.168.1.5:9443", link.DeviceBase);
        Assert.Equal("secret", link.Password);
    }

    [Fact]
    public void Parse_DirectMedia_IsNotSharePage()
    {
        Assert.False(UgreenShareLinkParser.IsSharePage("https://cdn.example.com/a.mp4"));
        Assert.False(UgreenShareLinkParser.IsSharePage(@"D:\video\a.mkv"));
    }
}
