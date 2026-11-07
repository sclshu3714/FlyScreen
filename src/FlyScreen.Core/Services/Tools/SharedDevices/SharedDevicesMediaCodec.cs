using System.Buffers.Binary;
using FlyScreen.Services.Network;

namespace FlyScreen.Services.Tools.SharedDevices;

/// <summary>共享设备 ToolData：魔数 + 种类 + 载荷（PCM 复用 ScreenExtendAudioCodec / 摄像头为 JPEG）。</summary>
public static class SharedDevicesMediaCodec
{
    public const uint Magic = 0x31544453; // "SDT1" LE
    public const byte KindSpeakerPcm = 1; // 申请端 → 提供方
    public const byte KindMicPcm = 2;     // 提供方 → 申请端
    public const byte KindCameraJpeg = 3; // 提供方 → 申请端

    public static byte[] EncodePcm(byte kind, Guid sessionId, int sampleRate, int channels, int seq, ReadOnlySpan<byte> pcm)
    {
        var inner = ScreenExtendAudioCodec.Encode(sessionId, sampleRate, channels, seq, pcm);
        var buf = new byte[5 + inner.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), Magic);
        buf[4] = kind;
        inner.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static byte[] EncodeCameraJpeg(ReadOnlySpan<byte> jpeg)
    {
        var buf = new byte[5 + jpeg.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), Magic);
        buf[4] = KindCameraJpeg;
        jpeg.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out byte kind, out ReadOnlySpan<byte> body)
    {
        kind = 0;
        body = default;
        if (payload.Length < 5) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != Magic) return false;
        kind = payload[4];
        body = payload[5..];
        return kind is KindSpeakerPcm or KindMicPcm or KindCameraJpeg;
    }
}
