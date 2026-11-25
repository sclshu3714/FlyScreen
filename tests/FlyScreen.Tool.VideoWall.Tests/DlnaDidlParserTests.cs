using System.Xml.Linq;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources.Dlna;
using Xunit;

namespace FlyScreen.Tool.VideoWall.Tests;

public class DlnaDidlParserTests
{
    [Fact]
    public void Parse_ContainersAndVideoItems()
    {
        const string didl = """
            <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
                       xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
              <container id="1" parentID="0">
                <dc:title>Videos</dc:title>
                <upnp:class>object.container.storageFolder</upnp:class>
              </container>
              <item id="2" parentID="0">
                <dc:title>Movie.mkv</dc:title>
                <upnp:class>object.item.videoItem</upnp:class>
                <res protocolInfo="http-get:*:video/x-matroska:*" size="100">http://192.168.1.10:8200/MediaItems/2.mkv</res>
              </item>
              <item id="3" parentID="0">
                <dc:title>Song.mp3</dc:title>
                <upnp:class>object.item.audioItem.musicTrack</upnp:class>
                <res protocolInfo="http-get:*:audio/mpeg:*">http://192.168.1.10:8200/MediaItems/3.mp3</res>
              </item>
            </DIDL-Lite>
            """;

        var nodes = DlnaDidlParser.Parse(didl);
        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, n => n.IsFolder && n.Title == "Videos");
        var video = Assert.Single(nodes, n => !n.IsFolder);
        Assert.Equal("Movie.mkv", video.Title);
        Assert.Equal("http://192.168.1.10:8200/MediaItems/2.mkv", video.HttpUrl);
        Assert.Equal("video/x-matroska", video.Mime);
        Assert.Equal(100, video.SizeBytes);
    }

    [Fact]
    public void PickBestRes_PrefersHttpGetVideo()
    {
        var xml = XElement.Parse("""
            <item xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <res protocolInfo="http-get:*:image/jpeg:*">http://h/preview.jpg</res>
              <res protocolInfo="http-get:*:video/mp4:*" size="50">http://h/a.mp4</res>
              <res protocolInfo="rtsp-rtp-udp:*:video/mpeg:*">rtsp://h/stream</res>
            </item>
            """);
        var ns = (XNamespace)"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        var best = DlnaDidlParser.PickBestRes(xml.Elements(ns + "res"));
        Assert.Equal("http://h/a.mp4", best.Url);
        Assert.Equal("video/mp4", best.Mime);
    }
}

public class DlnaSsdpClientTests
{
    [Fact]
    public void TryParseResponse_MediaServerLocation()
    {
        const string raw =
            "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            "LOCATION: http://192.168.1.10:8200/rootDesc.xml\r\n" +
            "ST: urn:schemas-upnp-org:device:MediaServer:1\r\n" +
            "USN: uuid:abc::urn:schemas-upnp-org:device:MediaServer:1\r\n" +
            "SERVER: Linux/1.0 UPnP/1.0 MiniDLNA/1.3\r\n" +
            "\r\n";

        Assert.True(DlnaSsdpClient.TryParseResponse(raw, out var hit));
        Assert.Equal("http://192.168.1.10:8200/rootDesc.xml", hit.Location.AbsoluteUri);
        Assert.Contains("MiniDLNA", hit.ServerHeader);
    }

    [Fact]
    public void TryParseResponse_RejectsNonMediaServer()
    {
        const string raw =
            "HTTP/1.1 200 OK\r\n" +
            "LOCATION: http://192.168.1.10:8080/desc.xml\r\n" +
            "ST: upnp:rootdevice\r\n" +
            "\r\n";
        Assert.False(DlnaSsdpClient.TryParseResponse(raw, out _));
    }
}

public class DlnaMediaServerClientTests
{
    [Fact]
    public void ParseDescription_FindsContentDirectory()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                <friendlyName>UGREEN-NAS</friendlyName>
                <UDN>uuid:test-udn</UDN>
                <manufacturer>UGREEN</manufacturer>
                <modelName>DXP</modelName>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
                    <controlURL>/ctl/ContentDir</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var bound = DlnaMediaServerClient.ParseDescription(
            new Uri("http://192.168.1.10:8200/rootDesc.xml"), xml);
        Assert.Equal("UGREEN-NAS", bound.Server.FriendlyName);
        Assert.Equal("uuid:test-udn", bound.Server.Udn);
        Assert.Equal("http://192.168.1.10:8200/ctl/ContentDir", bound.ControlUrl.AbsoluteUri);
    }

    [Fact]
    public void ParseBrowseResponse_OrdersFoldersFirst()
    {
        const string soap = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <Result><![CDATA[
                    <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
                               xmlns:dc="http://purl.org/dc/elements/1.1/"
                               xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
                      <item id="v1" parentID="0">
                        <dc:title>B.mp4</dc:title>
                        <upnp:class>object.item.videoItem</upnp:class>
                        <res protocolInfo="http-get:*:video/mp4:*">http://h/B.mp4</res>
                      </item>
                      <container id="c1" parentID="0">
                        <dc:title>A-Folder</dc:title>
                        <upnp:class>object.container</upnp:class>
                      </container>
                    </DIDL-Lite>
                  ]]></Result>
                  <NumberReturned>2</NumberReturned>
                  <TotalMatches>2</TotalMatches>
                </u:BrowseResponse>
              </s:Body>
            </s:Envelope>
            """;

        var page = DlnaMediaServerClient.ParseBrowseResponse(soap, "0", 0, 50);
        Assert.Equal(2, page.Nodes.Count);
        Assert.True(page.Nodes[0].IsFolder);
        Assert.False(page.Nodes[1].IsFolder);
        Assert.False(page.HasMore);
    }
}

public class DlnaImportServiceTests
{
    [Fact]
    public void BuildSource_UsesHttpUrlAndGroup()
    {
        var server = new DlnaServer("uuid:1", "NAS", new Uri("http://192.168.1.1:8200/d.xml"), null, null);
        var node = new DlnaNode("id", "0", false, "Film", "http://192.168.1.1:8200/a.mkv", "video/x-matroska", 10, "object.item.videoItem");
        var src = DlnaImportService.BuildSource(server, node);
        Assert.Equal("Film", src.Name);
        Assert.Equal(VideoSourceKind.NetworkUrl, src.Kind);
        Assert.Equal("http://192.168.1.1:8200/a.mkv", src.Endpoint.Address);
        Assert.Equal("DLNA / NAS", src.GroupId);
        Assert.Equal("software", src.DecodePreference);
        Assert.Equal(1200, src.Endpoint.BufferMs);
    }

    [Fact]
    public void ListEmptySlotIndexes_SkipsOccupied()
    {
        string?[] ids = ["a", null, "b", ""];
        var empty = DlnaImportService.ListEmptySlotIndexes(4, ids);
        Assert.Equal([1, 3], empty.ToArray());
    }
}
