using System.Text;
using System.Text.Json;
using FlyScreen.Services.Network;
using Xunit;

namespace FlyScreen.Core.Tests;

public class HandshakeCapsTests
{
    [Fact]
    public void Enrich_SetsProtocolV2AndCaps()
    {
        LocalPeerIdentity.Configure(
            platform: PeerPlatform.Windows,
            deviceClass: PeerDeviceClass.Desktop,
            caps: PeerCaps.DesktopFull);

        var hs = LocalPeerIdentity.Enrich(new HandshakePayload
        {
            MachineName = "PC-A",
            SecurityKey = "secret"
        });

        Assert.Equal(2, hs.ProtocolVersion);
        Assert.Equal(PeerPlatform.Windows, hs.Platform);
        Assert.Equal(PeerDeviceClass.Desktop, hs.DeviceClass);
        Assert.Contains(PeerCaps.EdgeMouse, hs.Caps!);
        Assert.False(string.IsNullOrEmpty(hs.AppVersion));
    }

    [Fact]
    public void IntersectCaps_LegacyEmpty_ReturnsLocal()
    {
        LocalPeerIdentity.Configure(caps: PeerCaps.DesktopFull);
        var effective = LocalPeerIdentity.IntersectCaps(null);
        Assert.Contains(PeerCaps.EdgeMouse, effective);

        effective = LocalPeerIdentity.IntersectCaps([]);
        Assert.Contains(PeerCaps.InjectInput, effective);
    }

    [Fact]
    public void IntersectCaps_AndroidMobile_EdgeMouseAndInject_NoLayoutSync()
    {
        LocalPeerIdentity.Configure(caps: PeerCaps.DesktopFull);
        var effective = LocalPeerIdentity.IntersectCaps(PeerCaps.AndroidMobile);
        Assert.Contains(PeerCaps.EdgeMouse, effective);
        Assert.Contains(PeerCaps.InjectInput, effective);
        Assert.Contains(PeerCaps.AbsPointer, effective);
        Assert.Contains(PeerCaps.BulkTransfer, effective);
        Assert.DoesNotContain(PeerCaps.LayoutSync, effective);
    }

    [Fact]
    public void BulkMessageTypes_FileAndClipboardChunks_AreBulk()
    {
        Assert.True(BulkMessageTypes.IsBulk(MessageType.FileChunk));
        Assert.True(BulkMessageTypes.IsBulk(MessageType.ClipboardInlineChunk));
        Assert.True(BulkMessageTypes.IsBulk(MessageType.ClipboardImageChunk));
        Assert.False(BulkMessageTypes.IsBulk(MessageType.MouseMove));
        Assert.False(BulkMessageTypes.IsBulk(MessageType.ClipboardFilesAnnounce));
        Assert.False(BulkMessageTypes.IsBulk(MessageType.ClipboardFilesPull));
    }

    [Fact]
    public void DesktopFull_IncludesBulkTransfer()
    {
        Assert.Contains(PeerCaps.BulkTransfer, PeerCaps.DesktopFull);
        Assert.Contains(PeerCaps.BulkTransfer, PeerCaps.AndroidMobile);
    }

    [Fact]
    public void AndroidMobile_IncludesScreenExtendHost_NotSink()
    {
        Assert.Contains(PeerCaps.ScreenExtendHost, PeerCaps.AndroidMobile);
        Assert.DoesNotContain(PeerCaps.ScreenExtendSink, PeerCaps.AndroidMobile);
        Assert.Contains(PeerCaps.ScreenExtendHost, PeerCaps.HarmonyMobile);
        Assert.DoesNotContain(PeerCaps.ScreenExtendSink, PeerCaps.HarmonyMobile);
    }

    [Fact]
    public void FromHandshake_LegacyPayload_IsLegacy()
    {
        var json = """{"MachineName":"OLD","SecurityKey":"k","LocalIp":"1.2.3.4","ProtocolVersion":1}""";
        var hs = JsonSerializer.Deserialize<HandshakePayload>(json);
        var info = PeerRemoteInfo.FromHandshake(hs);
        Assert.True(info.IsLegacy);
        Assert.Empty(info.Caps);
    }

    [Fact]
    public void FromHandshake_V2WithCaps_Parsed()
    {
        var json = """
            {"MachineName":"PHONE","SecurityKey":"k","ProtocolVersion":2,
             "Platform":"Android","DeviceClass":"Phone",
             "Caps":["AbsPointer","ClipboardText","DirectTransfer"],"AppVersion":"1.4.0"}
            """;
        var hs = JsonSerializer.Deserialize<HandshakePayload>(json);
        var info = PeerRemoteInfo.FromHandshake(hs);
        Assert.False(info.IsLegacy);
        Assert.Equal("Android", info.Platform);
        Assert.Equal("Phone", info.DeviceClass);
        Assert.Contains(PeerCaps.AbsPointer, info.Caps);
    }

    [Fact]
    public void MessageCodec_RoundTrip_PreservesExtraFieldsForOldReader()
    {
        // 新端编码带 Caps
        LocalPeerIdentity.Configure(caps: PeerCaps.DesktopFull);
        var frame = MessageCodec.EncodeJson(MessageType.Handshake, LocalPeerIdentity.Enrich(new HandshakePayload
        {
            MachineName = "NEW",
            SecurityKey = "key"
        }));

        Assert.Equal((byte)MessageType.Handshake, frame[0]);
        var len = BitConverter.ToInt32(frame, 1);
        var payload = frame.AsSpan(5, len);
        var text = Encoding.UTF8.GetString(payload);
        Assert.Contains("Caps", text);
        Assert.Contains("Platform", text);

        // 旧端模型只有基础字段时：System.Text.Json 默认忽略多余属性
        var slim = JsonSerializer.Deserialize<LegacyHandshake>(text);
        Assert.NotNull(slim);
        Assert.Equal("NEW", slim!.MachineName);
        Assert.Equal("key", slim.SecurityKey);
    }

    private sealed class LegacyHandshake
    {
        public string MachineName { get; set; } = "";
        public string SecurityKey { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public int ProtocolVersion { get; set; }
    }
}
