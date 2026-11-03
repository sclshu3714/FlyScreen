using System.Buffers.Binary;
using FlyScreen.Services.Network;
using Xunit;

namespace FlyScreen.Core.Tests;

public class ScreenExtendAudioCodecTests
{
    [Fact]
    public void Roundtrip_keeps_pcm()
    {
        var sid = Guid.NewGuid();
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
        var buf = ScreenExtendAudioCodec.Encode(sid, 48000, 2, 7, pcm);
        Assert.True(ScreenExtendAudioCodec.TryDecode(buf, out var id, out var rate, out var ch, out var seq, out var body));
        Assert.Equal(sid, id);
        Assert.Equal(48000, rate);
        Assert.Equal(2, ch);
        Assert.Equal(7, seq);
        Assert.Equal(pcm, body.ToArray());
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(22, 2)));
    }
}
